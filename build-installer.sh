#!/usr/bin/env bash
# Build Release DLLs and package them into Setup.exe via Inno Setup.
# Bash counterpart to installer/build-installer.cmd — run from Git Bash.
#
# Usage:
#   ./build-installer.sh

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

echo "=== Building RevitWallsPlugin (Release) ==="
# -p: form (not /p:) so Git Bash MSYS doesn't rewrite the switch as a path.
dotnet build -c Release

# Locate ISCC.exe (Inno Setup 6 compiler).
ISCC="C:/Program Files (x86)/Inno Setup 6/ISCC.exe"
if [[ ! -f "$ISCC" ]]; then
    ISCC="C:/Program Files/Inno Setup 6/ISCC.exe"
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

echo
echo "Done. Installer in installer/Output/"
