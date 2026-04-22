#!/usr/bin/env bash
# Build RevitWallsPlugin and install it into the per-user Revit add-ins folder.
# If Revit is running, it will be closed before the build and relaunched afterwards.
#
# Usage:
#   ./install.sh              # Revit 2025 (default)
#   ./install.sh 2026         # any installed Revit year
#   ./install.sh 2025 --no-restart    # do not relaunch Revit after install
#
# Requires: dotnet SDK, Revit <year> installed at C:\Program Files\Autodesk\Revit <year>\.

set -euo pipefail

YEAR="${1:-2025}"
RESTART_FLAG="${2:-}"
CONFIG="Release"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

REVIT_DIR="C:/Program Files/Autodesk/Revit ${YEAR}"
REVIT_EXE="${REVIT_DIR}/Revit.exe"

if [[ ! -f "${REVIT_DIR}/RevitAPI.dll" ]]; then
    echo "ERROR: Revit ${YEAR} not found at ${REVIT_DIR}" >&2
    echo "Install Revit ${YEAR}, or pass a year that is installed: ./install.sh 2025" >&2
    exit 1
fi

# --- Stop Revit if it is running, and remember that we did. ---
revit_was_running=0
if tasklist.exe //FI "IMAGENAME eq Revit.exe" 2>/dev/null | grep -qi "Revit.exe"; then
    revit_was_running=1
    echo ">>> Revit is running — stopping it (it locks the plug-in DLL)"
    taskkill.exe //IM Revit.exe //T //F >/dev/null 2>&1 || true

    # Wait for the process to actually disappear before we try to overwrite the DLL.
    for _ in 1 2 3 4 5 6 7 8 9 10; do
        if tasklist.exe //FI "IMAGENAME eq Revit.exe" 2>/dev/null | grep -qi "Revit.exe"; then
            sleep 1
        else
            break
        fi
    done

    if tasklist.exe //FI "IMAGENAME eq Revit.exe" 2>/dev/null | grep -qi "Revit.exe"; then
        echo "ERROR: Could not stop Revit within 10s. Close it manually and retry." >&2
        exit 1
    fi
fi

# --- Build. ---
echo ">>> Building RevitWallsPlugin for Revit ${YEAR} (${CONFIG})"
# Use -p: form (not /p:) so Git Bash MSYS doesn't rewrite the switch as a path.
dotnet build -c "${CONFIG}" -p:RevitVersion="${YEAR}"

BIN_DIR="${SCRIPT_DIR}/bin/${CONFIG}"
DLL="${BIN_DIR}/RevitWallsPlugin.dll"
ADDIN="${BIN_DIR}/RevitWallsPlugin.addin"

if [[ ! -f "${DLL}" || ! -f "${ADDIN}" ]]; then
    echo "ERROR: Build output missing: ${DLL} or ${ADDIN}" >&2
    exit 1
fi

# --- Remove the exe-installer version so Revit doesn't load two copies. ---
# The exe installer writes %ProgramData%\Autodesk\Revit\Addins\<year>\BIMy.addin,
# which references the Program Files DLL. If left in place alongside the dev
# install, Revit loads both and trips on duplicate ClientIds.
PROGDATA="${PROGRAMDATA:-C:/ProgramData}"
EXE_ADDIN="${PROGDATA}/Autodesk/Revit/Addins/${YEAR}/BIMy.addin"
if [[ -f "${EXE_ADDIN}" ]]; then
    echo ">>> Removing exe-installed addin: ${EXE_ADDIN}"
    if ! rm -f "${EXE_ADDIN}" 2>/dev/null; then
        echo "WARNING: Could not remove ${EXE_ADDIN} (permission denied)."
        echo "         Uninstall 'BIMy for Revit' via Windows 'Add/Remove Programs'"
        echo "         or re-run this script from an elevated shell."
    fi
fi

# --- Install. ---
DEST="${APPDATA}/Autodesk/Revit/Addins/${YEAR}"
mkdir -p "${DEST}"

echo ">>> Installing to ${DEST}"
cp -f "${DLL}"   "${DEST}/"
cp -f "${ADDIN}" "${DEST}/"

echo
echo "Installed:"
echo "  ${DEST}/RevitWallsPlugin.dll"
echo "  ${DEST}/RevitWallsPlugin.addin"
echo
echo "Log file will be written next to the DLL:"
echo "  ${DEST}/RevitWallsPlugin.log"

# --- Relaunch Revit if we stopped it (and the user didn't opt out). ---
if [[ "${revit_was_running}" -eq 1 && "${RESTART_FLAG}" != "--no-restart" ]]; then
    echo
    echo ">>> Relaunching Revit ${YEAR}"
    cmd.exe //c start "" "$(cygpath -w "${REVIT_EXE}")"
else
    echo
    echo "Launch Revit ${YEAR} → open a project → Add-Ins → External Tools → Import Walls From URL."
fi
