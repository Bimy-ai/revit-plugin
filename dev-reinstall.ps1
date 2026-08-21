<#
.SYNOPSIS
  Build the add-in, reinstall it into Revit, and relaunch Revit — one command.

.DESCRIPTION
  The inner development loop for this plugin is: change C#, get it in front of
  Revit, look at it. Doing that by hand is six steps (close Revit, build, find
  the Addins folder for the right year, copy two artefacts, start Revit, wait),
  and the two easy ones to get wrong — copying into the wrong year's folder, and
  forgetting that Revit holds the DLL open — both fail quietly: Revit starts,
  the ribbon looks right, and you are testing the previous build.

  Two modes:

    -Fast     Build + copy straight into %AppData%\Autodesk\Revit\Addins\<year>\.
              Seconds. This is what you want while iterating.

    (default) Build + compile the Inno Setup installer + run it silently. Slower,
              but it exercises the artefact real users get, so it is what you
              want before shipping. The Setup.exe is left in installer\Output\.

  Revit is closed before either mode (it locks the DLL) and relaunched after,
  unless -NoRestart. Because closing Revit can lose unsaved work, the script
  asks first — pass -Force to skip the prompt in scripted runs.

  This script never uploads. Publishing the installer to GCS stays in
  build-installer.ps1, so a dev-loop reinstall can't accidentally ship a build.

.PARAMETER RevitVersion
  Revit year to build against and install into. Default: newest detected.

.PARAMETER Fast
  Skip the installer; copy the build output into the Addins folder directly.

.PARAMETER AllUsers
  Install machine-wide rather than for the current Windows account. Needs
  elevation: in fast mode it also writes %ProgramData%\Autodesk\Revit\Addins\,
  and in installer mode it runs Setup with /ALLUSERS.

.PARAMETER Bump
  Increment the patch component of AppVersion in installer\BIMy.iss before
  packaging, so each installer build produces a distinctly named Setup.exe.

.PARAMETER NoRestart
  Leave Revit closed when finished.

.PARAMETER Force
  Don't ask before closing a running Revit.

.EXAMPLE
  pwsh -File dev-reinstall.ps1 -Fast

.EXAMPLE
  pwsh -File dev-reinstall.ps1 -Bump          # full installer, version bumped
#>
[CmdletBinding()]
param(
    [string]$RevitVersion = $env:REVIT_VERSION,
    [switch]$Fast,
    [switch]$AllUsers,
    [switch]$Bump,
    [switch]$NoRestart,
    [switch]$Force,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($PSVersionTable.PSEdition -ne 'Core') {
    Write-Error "This script needs PowerShell 7+ (pwsh). Run: pwsh -File dev-reinstall.ps1"
    exit 1
}

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $ScriptDir

function Write-Step([string]$Text) { Write-Host "`n=== $Text ===" -ForegroundColor Cyan }

# ─── Detect Revit ────────────────────────────────────────────────────────────
# Probe for RevitAPI.dll, not just the folder: an upgraded machine can leave an
# empty "Revit 2025\" behind that would build against nothing.
$RevitRoot = if ($env:REVIT_ROOT) { $env:REVIT_ROOT } else { "$env:ProgramFiles\Autodesk" }
$AllYears = @()
foreach ($year in 2030, 2029, 2028, 2027, 2026, 2025, 2024, 2023, 2022) {
    if (Test-Path "$RevitRoot\Revit $year\RevitAPI.dll") { $AllYears += "$year" }
}
if (-not $RevitVersion) { $RevitVersion = $AllYears | Select-Object -First 1 }
if (-not $RevitVersion) {
    Write-Error "No Revit install with RevitAPI.dll found under '$RevitRoot'. Set REVIT_VERSION or install Revit."
    exit 1
}
$RevitExe = "$RevitRoot\Revit $RevitVersion\Revit.exe"
Write-Host "Revit $RevitVersion  ($RevitExe)"

# ─── Close Revit ─────────────────────────────────────────────────────────────
# Revit maps the plugin DLL into its process, so every deployment path — copy or
# installer — fails while it runs. Capture whether it WAS running so we only
# relaunch something the user actually had open.
$revitProcs = @(Get-Process -Name 'Revit' -ErrorAction SilentlyContinue)
$revitWasRunning = $revitProcs.Count -gt 0

if ($revitWasRunning) {
    if (-not $Force) {
        Write-Host "Revit is running. Closing it will discard anything unsaved." -ForegroundColor Yellow
        $answer = Read-Host "Close Revit and continue? [y/N]"
        if ($answer -notmatch '^(y|yes)$') { Write-Host 'Aborted.'; exit 1 }
    }

    Write-Step 'Closing Revit'
    # Ask politely first so Revit gets the chance to run its own shutdown; only
    # then force it. CloseMainWindow returns immediately, hence the wait.
    foreach ($p in $revitProcs) { try { $null = $p.CloseMainWindow() } catch { } }
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    while ((Get-Process -Name 'Revit' -ErrorAction SilentlyContinue) -and [DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 400
    }
    $still = @(Get-Process -Name 'Revit' -ErrorAction SilentlyContinue)
    if ($still.Count -gt 0) {
        Write-Host '  Revit did not exit (a modal dialog usually) — terminating.' -ForegroundColor Yellow
        foreach ($p in $still) { try { $p.Kill() } catch { } }
        Start-Sleep -Seconds 2
    }
}

# ─── Build ───────────────────────────────────────────────────────────────────
Write-Step "Building BimyRevit ($Configuration, Revit $RevitVersion)"
& dotnet build -c $Configuration -p:RevitVersion=$RevitVersion --disable-build-servers -nologo
if ($LASTEXITCODE -ne 0) { Write-Error "Build failed (dotnet exit $LASTEXITCODE)."; exit 1 }

$BinDir = Join-Path $ScriptDir "bin\$Configuration"
$Dll = Join-Path $BinDir 'BimyRevit.dll'
if (-not (Test-Path $Dll)) { Write-Error "Build produced no DLL at $Dll."; exit 1 }

# ─── Deploy ──────────────────────────────────────────────────────────────────
if ($Fast) {
    # Mirror exactly what installer\BIMy.iss lays down:
    #   <Addins>\<year>\BIMy.addin           (manifest, relative <Assembly> path)
    #   <Addins>\<year>\BIMy\*.dll           (payload)
    # so what you test in the fast loop is the same layout users get.
    $roots = @("$env:APPDATA\Autodesk\Revit\Addins\$RevitVersion")
    if ($AllUsers) { $roots += "$env:ProgramData\Autodesk\Revit\Addins\$RevitVersion" }

    $manifest = Join-Path $ScriptDir 'installer\BIMy.addin.template'
    if (-not (Test-Path $manifest)) { Write-Error "Missing $manifest."; exit 1 }

    Write-Step 'Deploying to Revit Addins'
    foreach ($root in $roots) {
        $pluginDir = Join-Path $root 'BIMy'
        try {
            New-Item -ItemType Directory -Force -Path $pluginDir | Out-Null
            # Same legacy sweep the installer does: a folder deployed before the
            # rename still holds RevitWallsPlugin.*, which nothing references but
            # everything looks like.
            foreach ($stale in 'RevitWallsPlugin.dll', 'RevitWallsPlugin.deps.json', 'RevitWallsPlugin.pdb') {
                Remove-Item (Join-Path $pluginDir $stale) -Force -ErrorAction SilentlyContinue
            }
            foreach ($name in 'BimyRevit.dll', 'BimyRevit.deps.json', 'BimyRevit.pdb') {
                $src = Join-Path $BinDir $name
                if (Test-Path $src) { Copy-Item $src -Destination $pluginDir -Force }
            }
            Copy-Item $manifest -Destination (Join-Path $root 'BIMy.addin') -Force
            Write-Host "  $root"
        } catch {
            # A denied ProgramData write is expected without elevation; say so
            # rather than failing the whole run when the per-user copy worked.
            Write-Host "  SKIPPED $root — $($_.Exception.Message)" -ForegroundColor Yellow
        }
    }
} else {
    if ($Bump) {
        Write-Step 'Bumping installer version'
        $issPath = Join-Path $ScriptDir 'installer\BIMy.iss'
        $iss = Get-Content -Raw $issPath
        if ($iss -notmatch '#define\s+AppVersion\s+"(\d+)\.(\d+)\.(\d+)"') {
            Write-Error "Could not find an AppVersion define in $issPath."
            exit 1
        }
        $bumped = "{0}.{1}.{2}" -f $Matches[1], $Matches[2], ([int]$Matches[3] + 1)
        $iss = $iss -replace '(#define\s+AppVersion\s+")\d+\.\d+\.\d+(")', "`${1}$bumped`${2}"
        # -NoNewline: Get-Content -Raw already carries the trailing newline, and
        # Set-Content would otherwise append a second one on every bump.
        Set-Content -Path $issPath -Value $iss -NoNewline
        Write-Host "  AppVersion -> $bumped"
    }

    $ISCC = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $ISCC) {
        Write-Error "Inno Setup 6 not found. 'winget install JRSoftware.InnoSetup', or use -Fast to skip the installer."
        exit 1
    }

    Write-Step 'Packaging installer'
    # The .iss reads from bin\Release regardless of -Configuration, so a Debug
    # build can't be packaged — say it plainly instead of shipping stale bits.
    if ($Configuration -ne 'Release') {
        Write-Error "installer\BIMy.iss packages bin\Release. Build Release, or use -Fast for a Debug loop."
        exit 1
    }
    & $ISCC (Join-Path $ScriptDir 'installer\BIMy.iss')
    if ($LASTEXITCODE -ne 0) { Write-Error "ISCC failed (exit $LASTEXITCODE)."; exit 1 }

    $setup = Get-ChildItem (Join-Path $ScriptDir 'installer\Output\*.exe') -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $setup) { Write-Error 'ISCC produced no .exe in installer\Output\.'; exit 1 }

    Write-Step "Running $($setup.Name)"
    # /VERYSILENT + /SUPPRESSMSGBOXES so nothing waits for a click (the .iss uses
    # SuppressibleMsgBox throughout so this actually works), and an EXPLICIT
    # scope: BIMy.iss sets PrivilegesRequiredOverridesAllowed=dialog, and a
    # silent run has no way to show that dialog — Setup answers it as "cancel"
    # and exits 2 without installing anything. Saying which scope we want makes
    # the dialog unnecessary.
    $scope = if ($AllUsers) { '/ALLUSERS' } else { '/CURRENTUSER' }
    $proc = Start-Process -FilePath $setup.FullName `
        -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', $scope -Wait -PassThru
    if ($proc.ExitCode -ne 0) { Write-Error "Installer exited with $($proc.ExitCode)."; exit 1 }
    Write-Host "  Installed from $($setup.FullName)"
}

# ─── Verify ──────────────────────────────────────────────────────────────────
# Deployment can "succeed" and still leave Revit loading yesterday's DLL, so
# check what actually landed rather than trusting the copy.
Write-Step 'Verifying'
$installed = Join-Path "$env:APPDATA\Autodesk\Revit\Addins\$RevitVersion\BIMy" 'BimyRevit.dll'
if (Test-Path $installed) {
    $built = (Get-Item $Dll).LastWriteTimeUtc
    $live = (Get-Item $installed).LastWriteTimeUtc
    Write-Host "  $installed"
    Write-Host ("  built {0:u}   installed {1:u}" -f $built, $live)
    # Two seconds of slack: Inno's CopyFile preserves the source timestamp but
    # rounds it to FAT granularity, so an exact comparison flags every healthy
    # installer run as stale. A warning that always fires is worse than none.
    if ($live -lt $built.AddSeconds(-2)) {
        Write-Host '  WARNING: the installed DLL is older than the one just built.' -ForegroundColor Yellow
    }
} else {
    Write-Host "  WARNING: nothing at $installed" -ForegroundColor Yellow
}

# ─── Relaunch ────────────────────────────────────────────────────────────────
if ($NoRestart) {
    Write-Host "`nDone. Revit not restarted (-NoRestart)."
} elseif ($revitWasRunning -or $Force) {
    Write-Step 'Starting Revit'
    Start-Process -FilePath $RevitExe | Out-Null
    Write-Host '  Revit is starting. The BIMy panel appears on the Add-Ins tab.'
} else {
    Write-Host "`nDone. Revit wasn't running, so it wasn't started — pass -Force to launch it anyway."
}

Write-Host "`nLog: $env:LOCALAPPDATA\BIMy\bimy.log" -ForegroundColor DarkGray
