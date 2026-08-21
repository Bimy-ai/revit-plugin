<#
.SYNOPSIS
  Build the Release DLLs, package them into Setup.exe via Inno Setup, and
  upload the installer to Google Cloud Storage.

.DESCRIPTION
  Native PowerShell implementation of the build+package+upload pipeline.

  Why PowerShell and not the bash script: when build-installer.sh runs under
  Git Bash (MSYS), every native command it shells out to — dotnet, ISCC, and
  especially the openssl/curl/awk/sed swarm used to sign the GCS JWT — is a
  fresh Win32 process. On some machines MSYS fork() intermittently fails with
  0xC0000005 access violations, which manifests as blank, unclosable console
  windows and an apparent hang. Running everything inside ONE PowerShell
  process eliminates the fork swarm: dotnet/ISCC run as child console apps
  (no new windows), and the GCS auth + upload is done entirely in-process via
  .NET crypto and Invoke-WebRequest (no openssl, no curl).

  Requires PowerShell 7+ (pwsh) — RSA.ImportFromPem is .NET 5+ only and is not
  available in Windows PowerShell 5.1.

.PARAMETER RevitVersion
  Force a specific Revit API year to build against. Default: newest detected.

.PARAMETER SkipUpload
  Build and package only; don't upload to GCS.

.EXAMPLE
  pwsh -File build-installer.ps1
#>
[CmdletBinding()]
param(
    [string]$RevitVersion = $env:REVIT_VERSION,
    [switch]$SkipUpload
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# ─── Re-launch under PowerShell 7 if started by Windows PowerShell ───────────
# The Start-menu "PowerShell", right-click "Run with PowerShell", and most IDE
# terminals are still Windows PowerShell 5.1, where RSA.ImportFromPem (.NET 5+)
# doesn't exist. Rather than dead-ending with an error, hand the run off to
# pwsh. -ExecutionPolicy Bypass is passed because the child would otherwise
# inherit a Restricted policy and refuse to load this file.
if ($PSVersionTable.PSEdition -ne 'Core') {
    $pwsh = (Get-Command pwsh.exe -ErrorAction SilentlyContinue).Source
    if (-not $pwsh) {
        $pwsh = @(
            "$env:ProgramFiles\PowerShell\7\pwsh.exe",
            "${env:ProgramFiles(x86)}\PowerShell\7\pwsh.exe"
        ) | Where-Object { Test-Path $_ } | Select-Object -First 1
    }
    if (-not $pwsh) {
        Write-Error "This script needs PowerShell 7+, but pwsh.exe was not found. Install it with 'winget install Microsoft.PowerShell' and re-run."
        exit 1
    }
    Write-Host "Detected Windows PowerShell $($PSVersionTable.PSVersion) - relaunching under PowerShell 7..."
    $fwd = @()
    if ($RevitVersion) { $fwd += @('-RevitVersion', $RevitVersion) }
    if ($SkipUpload)   { $fwd += '-SkipUpload' }
    & $pwsh -NoProfile -ExecutionPolicy Bypass -File $PSCommandPath @fwd
    exit $LASTEXITCODE
}

# $PSCommandPath, unlike $MyInvocation.MyCommand.Path, stays correct when the
# script is dot-sourced rather than invoked by path.
$ScriptDir = Split-Path -Parent $PSCommandPath
Set-Location $ScriptDir

# ─── Detect Revit API ────────────────────────────────────────────────────────
# The csproj defaults to 2025 but that folder is sometimes a shell with no
# RevitAPI.dll (e.g. an upgraded machine). Probe newest-first so we build
# against the most modern API available; the installer deploys to every
# detected year at install time.
$RevitRoot = if ($env:REVIT_ROOT) { $env:REVIT_ROOT } else { "$env:ProgramFiles\Autodesk" }
if (-not $RevitVersion) {
    foreach ($year in 2030, 2029, 2028, 2027, 2026, 2025, 2024, 2023, 2022) {
        if (Test-Path "$RevitRoot\Revit $year\RevitAPI.dll") { $RevitVersion = "$year"; break }
    }
}
if (-not $RevitVersion) {
    Write-Error "No Revit install with RevitAPI.dll was found under '$RevitRoot'. Install Revit (any year 2022-2030) and re-run, or set REVIT_VERSION=<year>."
    exit 1
}

# ─── Build ─────────────────────────────────────────────────────────────────
# --disable-build-servers keeps MSBuild / Roslyn / Razor servers from
# outliving this script and holding the console handle.
Write-Host "=== Building RevitWallsPlugin (Release) - targeting Revit $RevitVersion ==="
& dotnet build -c Release -p:RevitVersion=$RevitVersion --disable-build-servers
if ($LASTEXITCODE -ne 0) { Write-Error "Build failed (dotnet exit $LASTEXITCODE)."; exit 1 }

# ─── Locate ISCC (Inno Setup 6 compiler) ─────────────────────────────────────
# Winget can place it per-user under %LOCALAPPDATA%\Programs, so check that too.
$ISCC = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $ISCC) {
    Write-Error "Inno Setup 6 was not found. Install it from https://jrsoftware.org/isdl.php (or 'winget install JRSoftware.InnoSetup') and re-run."
    exit 1
}

Write-Host "=== Packaging installer ==="
& $ISCC "$ScriptDir\installer\BIMy.iss"
if ($LASTEXITCODE -ne 0) { Write-Error "ISCC failed (exit $LASTEXITCODE)."; exit 1 }
Write-Host "=== ISCC finished ==="

# ─── Find the produced installer ─────────────────────────────────────────────
$InstallerExe = Get-ChildItem "$ScriptDir\installer\Output\*.exe" -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $InstallerExe) {
    Write-Error "ISCC did not produce an .exe in installer\Output\."
    exit 1
}

if ($SkipUpload) {
    Write-Host ""
    Write-Host "Done (upload skipped). Installer: $($InstallerExe.FullName)"
    exit 0
}

# ─── Upload to GCS ───────────────────────────────────────────────────────────
# Authenticate with a service-account key and push the freshly built Setup.exe
# into the bimy-common-assets bucket, overwriting any prior object with the
# same name. The key is looked up via $GCP_KEY_FILE, else the first well-known
# location that exists. Soft-fails when no key is found so contributors without
# GCS access can still build locally.
function Convert-ToBase64Url([byte[]]$Bytes) {
    [Convert]::ToBase64String($Bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
}

function Send-ToGcs {
    param([string]$File, [string]$Bucket, [string]$Object)

    $keyFile = $env:GCP_KEY_FILE
    if (-not $keyFile) {
        foreach ($c in @("$env:USERPROFILE\bimy\infra\gcp.json", "$env:USERPROFILE\gcp.json")) {
            if (Test-Path $c) { $keyFile = $c; break }
        }
    }
    if (-not $keyFile -or -not (Test-Path $keyFile)) {
        Write-Host "Skipping GCS upload - service-account key not found (set GCP_KEY_FILE or place gcp.json at ~\bimy\infra\gcp.json)."
        return
    }

    Write-Host "  -> parsing service-account key..."
    $sa = Get-Content -Raw $keyFile | ConvertFrom-Json
    if (-not $sa.client_email -or -not $sa.private_key) {
        Write-Error "GCS upload failed: could not parse client_email/private_key from $keyFile."
        exit 1
    }

    # Build and RS256-sign a JWT entirely in .NET — no openssl subprocess.
    $now = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
    $exp = $now + 3600
    $header = '{"alg":"RS256","typ":"JWT"}'
    $claim = @{
        iss   = $sa.client_email
        scope = "https://www.googleapis.com/auth/devstorage.read_write"
        aud   = "https://oauth2.googleapis.com/token"
        exp   = $exp
        iat   = $now
    } | ConvertTo-Json -Compress

    $enc = [System.Text.Encoding]::UTF8
    $h64 = Convert-ToBase64Url $enc.GetBytes($header)
    $c64 = Convert-ToBase64Url $enc.GetBytes($claim)
    $signingInput = "$h64.$c64"

    Write-Host "  -> signing JWT with service-account key..."
    $rsa = [System.Security.Cryptography.RSA]::Create()
    # private_key is a PKCS#8 PEM; ConvertFrom-Json already turned the \n
    # escapes into real newlines, so ImportFromPem accepts it directly.
    $rsa.ImportFromPem($sa.private_key)
    $sigBytes = $rsa.SignData(
        $enc.GetBytes($signingInput),
        [System.Security.Cryptography.HashAlgorithmName]::SHA256,
        [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)
    $jwt = "$signingInput.$(Convert-ToBase64Url $sigBytes)"

    Write-Host "  -> exchanging JWT for OAuth2 access token..."
    $tokenResp = Invoke-RestMethod -Method Post -Uri "https://oauth2.googleapis.com/token" `
        -ContentType "application/x-www-form-urlencoded" `
        -Body @{
            grant_type = "urn:ietf:params:oauth:grant-type:jwt-bearer"
            assertion  = $jwt
        } -TimeoutSec 60
    $accessToken = $tokenResp.access_token
    if (-not $accessToken) {
        Write-Error "GCS upload failed: could not obtain access token."
        exit 1
    }

    Write-Host "=== Uploading $(Split-Path -Leaf $File) to gs://$Bucket/$Object ==="
    # uploadType=media with the same object name overwrites the existing blob.
    $uri = "https://storage.googleapis.com/upload/storage/v1/b/$Bucket/o?uploadType=media&name=$([uri]::EscapeDataString($Object))"
    $resp = Invoke-RestMethod -Method Post -Uri $uri -InFile $File `
        -Headers @{ Authorization = "Bearer $accessToken" } `
        -ContentType "application/octet-stream" -TimeoutSec 600
    Write-Host "Uploaded. Generation $($resp.generation), size $($resp.size) bytes."
}

Send-ToGcs -File $InstallerExe.FullName -Bucket "bimy-common-assets" -Object $InstallerExe.Name

Write-Host ""
Write-Host "Done. Installer in installer\Output\"
