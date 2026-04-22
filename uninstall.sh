#!/usr/bin/env bash
# Remove the development install of RevitWallsPlugin from the per-user Revit
# add-ins folder. Use this to clean up before testing the exe-based installer.
#
# Does NOT touch the exe-installed artifacts under Program Files — uninstall
# those via Windows "Add/Remove Programs".
#
# Usage:
#   ./uninstall.sh              # Revit 2025 (default)
#   ./uninstall.sh 2026         # any year
#   ./uninstall.sh all          # every year under %APPDATA%/Autodesk/Revit/Addins

set -euo pipefail

ARG="${1:-2025}"
ADDINS_ROOT="${APPDATA}/Autodesk/Revit/Addins"

if [[ ! -d "${ADDINS_ROOT}" ]]; then
    echo "Nothing to remove — ${ADDINS_ROOT} does not exist."
    exit 0
fi

remove_year() {
    local year="$1"
    local dir="${ADDINS_ROOT}/${year}"
    local dll="${dir}/RevitWallsPlugin.dll"
    local addin="${dir}/RevitWallsPlugin.addin"
    local log="${dir}/RevitWallsPlugin.log"
    local removed=0

    for f in "$dll" "$addin" "$log"; do
        if [[ -f "$f" ]]; then
            rm -f "$f"
            echo "  removed ${f}"
            removed=1
        fi
    done

    if [[ "$removed" -eq 0 ]]; then
        echo "  (nothing for Revit ${year})"
    fi
}

if [[ "${ARG}" == "all" ]]; then
    echo ">>> Removing RevitWallsPlugin dev install from every Revit year"
    shopt -s nullglob
    for year_dir in "${ADDINS_ROOT}"/*/; do
        year="$(basename "${year_dir}")"
        echo "Revit ${year}:"
        remove_year "${year}"
    done
else
    echo ">>> Removing RevitWallsPlugin dev install for Revit ${ARG}"
    remove_year "${ARG}"
fi

echo
echo "Done."
