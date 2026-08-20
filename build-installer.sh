#!/usr/bin/env bash
# Build the Release DLLs, package them into Setup.exe via Inno Setup, and
# upload the installer to GCS.
#
# This is a thin wrapper that hands the work to build-installer.ps1. The build
# pipeline shells out to several native Windows tools (dotnet, ISCC) and, for
# the GCS upload, used to drive a swarm of openssl/curl/awk/sed subprocesses.
# Under Git Bash (MSYS) those native spawns intermittently fail with fork()
# access violations — which surface as blank, unclosable console windows and
# an apparent hang. Running everything in a single PowerShell 7 process avoids
# the fork swarm entirely. See build-installer.ps1 for the implementation.
#
# Usage:
#   ./build-installer.sh                 # build + package + upload
#   ./build-installer.sh --SkipUpload    # build + package only
#   REVIT_VERSION=2026 ./build-installer.sh

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# Require PowerShell 7 (pwsh). Windows PowerShell 5.1 (powershell.exe) can't be
# used: the JWT signing relies on RSA.ImportFromPem, which is .NET 5+ only.
if ! command -v pwsh >/dev/null 2>&1; then
    echo "PowerShell 7 (pwsh) is required but was not found on PATH." >&2
    echo "Install it with:  winget install Microsoft.PowerShell" >&2
    exit 1
fi

# cygpath converts the MSYS path to a native Windows path pwsh understands.
PS1_WIN="$(cygpath -w "${SCRIPT_DIR}/build-installer.ps1")"

exec pwsh -NoProfile -ExecutionPolicy Bypass -File "$PS1_WIN" "$@"
