#!/usr/bin/env bash
# Build Release DLLs and package them into Setup.exe via Inno Setup.
# Bash counterpart to installer/build-installer.cmd — run from Git Bash.
#
# Usage:
#   ./build-installer.sh

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

# Auto-detect a Revit install with the actual API DLLs present. The csproj
# defaults to 2025 but that folder is sometimes a shell with no RevitAPI.dll
# (e.g. an upgraded machine). Probe newest-first so we build against the
# most modern API available; the installer itself deploys to every detected
# year at install time.
REVIT_ROOT="${REVIT_ROOT:-/c/Program Files/Autodesk}"
REVIT_VERSION="${REVIT_VERSION:-}"
if [[ -z "$REVIT_VERSION" ]]; then
    for year in 2030 2029 2028 2027 2026 2025 2024 2023 2022; do
        if [[ -f "$REVIT_ROOT/Revit $year/RevitAPI.dll" ]]; then
            REVIT_VERSION="$year"
            break
        fi
    done
fi

if [[ -z "$REVIT_VERSION" ]]; then
    echo
    echo "No Revit install with RevitAPI.dll was found under $REVIT_ROOT." >&2
    echo "Install Revit (any year 2022–2030) and re-run this script, or set" >&2
    echo "REVIT_VERSION=<year> if the DLLs live in a non-standard location." >&2
    exit 1
fi

echo "=== Building RevitWallsPlugin (Release) — targeting Revit $REVIT_VERSION ==="
# -p: form (not /p:) so Git Bash MSYS doesn't rewrite the switch as a path.
# --disable-build-servers keeps MSBuild / Roslyn / Razor servers from
# outliving this script. Without it they inherit the console handle and
# the parent prompt hangs waiting for them after the build finishes.
dotnet build -c Release -p:RevitVersion="$REVIT_VERSION" --disable-build-servers

# Locate ISCC.exe (Inno Setup 6 compiler). Winget can place it per-user
# under %LOCALAPPDATA%\Programs, so check that path too.
ISCC="C:/Program Files (x86)/Inno Setup 6/ISCC.exe"
if [[ ! -f "$ISCC" ]]; then
    ISCC="C:/Program Files/Inno Setup 6/ISCC.exe"
fi
if [[ ! -f "$ISCC" ]]; then
    ISCC="${LOCALAPPDATA:-$USERPROFILE/AppData/Local}/Programs/Inno Setup 6/ISCC.exe"
fi

if [[ ! -f "$ISCC" ]]; then
    echo
    echo "Inno Setup 6 was not found. Install it from:" >&2
    echo "    https://jrsoftware.org/isdl.php" >&2
    echo "and re-run this script." >&2
    exit 1
fi

echo "=== Packaging installer ==="
"$ISCC" "$(cygpath -w "${SCRIPT_DIR}/installer/BIMy.iss")"
echo "=== ISCC finished ==="

# ─── Upload the produced installer to GCS ────────────────────────────────────
# Authenticates with a service-account key at ~/gcp.json (if present) and
# pushes the freshly built Setup.exe into the bimy-common-assets bucket,
# overwriting any prior object with the same name. Soft-fails when the key
# is missing so contributors without GCS access can still build locally.
upload_to_gcs() {
    local file="$1" bucket="$2" object="$3"
    local key_file="${GCP_KEY_FILE:-${HOME}/gcp.json}"

    if [[ ! -f "$key_file" ]]; then
        echo "Skipping GCS upload — service-account key not found at $key_file."
        return 0
    fi

    # Extract client_email and private_key from the service-account JSON in a
    # single awk pass per field. awk avoids the sed|head pipeline that has
    # been observed to hang on Git Bash when head closes its stdin early
    # while sed is still streaming.
    local client_email private_key_raw private_key
    echo "  → parsing service-account key…"
    client_email=$(awk -F'"' '/"client_email"[[:space:]]*:/ {print $4; exit}' "$key_file")
    private_key_raw=$(awk -F'"' '/"private_key"[[:space:]]*:/ {print $4; exit}' "$key_file")
    # Service-account JSON stores the PEM as a single line with literal \n
    # escapes. Unescape via sed (the bash ${var//\\n/$'\n'} substitution is
    # unreliable on Cygwin and leaves stray backslashes in the PEM).
    private_key=$(printf '%s' "$private_key_raw" | sed 's/\\n/\n/g')

    if [[ -z "$client_email" || -z "$private_key" ]]; then
        echo "GCS upload failed: could not parse client_email/private_key from $key_file." >&2
        return 1
    fi

    b64url() { openssl base64 -e -A | tr '+/' '-_' | tr -d '='; }

    local now exp header claim h64 c64 signing_input sig jwt
    now=$(date +%s)
    exp=$((now + 3600))
    header='{"alg":"RS256","typ":"JWT"}'
    claim="{\"iss\":\"${client_email}\",\"scope\":\"https://www.googleapis.com/auth/devstorage.read_write\",\"aud\":\"https://oauth2.googleapis.com/token\",\"exp\":${exp},\"iat\":${now}}"
    h64=$(printf '%s' "$header" | b64url)
    c64=$(printf '%s' "$claim"  | b64url)
    signing_input="${h64}.${c64}"

    echo "  → signing JWT with service-account key…"
    local key_pem
    key_pem=$(mktemp)
    chmod 600 "$key_pem"
    printf '%s\n' "$private_key" > "$key_pem"
    sig=$(printf '%s' "$signing_input" | openssl dgst -sha256 -sign "$key_pem" -binary | b64url)
    rm -f "$key_pem"
    jwt="${signing_input}.${sig}"

    echo "  → exchanging JWT for OAuth2 access token…"
    local token_resp access_token
    token_resp=$(curl -sS --fail-with-body \
        --connect-timeout 30 --max-time 60 \
        -X POST https://oauth2.googleapis.com/token \
        --data-urlencode "grant_type=urn:ietf:params:oauth:grant-type:jwt-bearer" \
        --data-urlencode "assertion=${jwt}") || {
        echo "GCS upload failed: token request errored." >&2
        echo "$token_resp" >&2
        return 1
    }
    access_token=$(printf '%s' "$token_resp" | awk -F'"' '/access_token/ {for(i=1;i<=NF;i++) if($i=="access_token"){print $(i+2); exit}}')
    if [[ -z "$access_token" ]]; then
        echo "GCS upload failed: could not obtain access token." >&2
        echo "$token_resp" >&2
        return 1
    fi

    echo "=== Uploading $(basename "$file") to gs://${bucket}/${object} ==="
    # uploadType=media with the same object name overwrites the existing blob
    # (GCS treats this as creating a new generation in place). -T uploads the
    # file via PUT-style streaming — friendlier than --data-binary @file for
    # large payloads and on Git Bash, where --data-binary can buffer the
    # entire file in memory before sending.
    local body_log http_code curl_status
    body_log=$(mktemp)
    set +e
    http_code=$(curl -sS --fail-with-body \
        --connect-timeout 30 --max-time 600 \
        -o "$body_log" -w '%{http_code}' \
        -X POST \
        -H "Authorization: Bearer ${access_token}" \
        -H "Content-Type: application/octet-stream" \
        --data-binary "@${file}" \
        "https://storage.googleapis.com/upload/storage/v1/b/${bucket}/o?uploadType=media&name=${object}")
    curl_status=$?
    set -e
    if [[ $curl_status -ne 0 || "$http_code" != "200" ]]; then
        echo "GCS upload failed (curl=$curl_status, HTTP $http_code):" >&2
        cat "$body_log" >&2
        rm -f "$body_log"
        return 1
    fi
    rm -f "$body_log"
    echo "Uploaded."
}

# Pick the newest .exe under installer/Output without using a piped
# `ls | head` (that pipeline has been observed to hang on Git Bash).
shopt -s nullglob
INSTALLER_EXE=""
for f in "${SCRIPT_DIR}/installer/Output"/*.exe; do
    if [[ -z "$INSTALLER_EXE" || "$f" -nt "$INSTALLER_EXE" ]]; then
        INSTALLER_EXE="$f"
    fi
done
shopt -u nullglob
if [[ -z "$INSTALLER_EXE" ]]; then
    echo "ISCC did not produce an .exe in installer/Output/." >&2
    exit 1
fi
upload_to_gcs "$INSTALLER_EXE" "bimy-common-assets" "$(basename "$INSTALLER_EXE")"

echo
echo "Done. Installer in installer/Output/"
