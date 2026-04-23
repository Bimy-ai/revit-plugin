#!/usr/bin/env bash
# Fully remove RevitWallsPlugin / BIMy from the user's system. Covers both:
#   - Dev install (the ./install.sh script)  → flat files under
#       %APPDATA%\Autodesk\Revit\Addins\<year>\RevitWallsPlugin.{dll,addin,log}
#   - EXE install (installer\Output\BIMy-for-Revit-Setup-*.exe) → bundled layout
#       %APPDATA%\Autodesk\Revit\Addins\<year>\BIMy.addin
#       %APPDATA%\Autodesk\Revit\Addins\<year>\BIMy\RevitWallsPlugin.dll
#     plus an uninstaller + Add/Remove Programs entry under
#       %LOCALAPPDATA%\Programs\BIMy.ai\BIMy for Revit\unins000.exe
#
# Usage:
#   ./uninstall.sh              # wipe every Revit year, per-user + machine-wide
#   ./uninstall.sh 2025         # only clean Revit 2025 addin files
#   ./uninstall.sh --keep-exe   # skip running the EXE uninstaller (leaves the
#                                 Add/Remove Programs entry; only clears addin
#                                 files so you can reinstall fresh on top)

set -euo pipefail

ARG="${1:-all}"
KEEP_EXE=0
if [[ "${ARG}" == "--keep-exe" ]]; then
    KEEP_EXE=1
    ARG="all"
fi

APPDATA_ROOT="${APPDATA}/Autodesk/Revit/Addins"
PROGDATA_ROOT="${PROGRAMDATA:-C:/ProgramData}/Autodesk/Revit/Addins"
EXE_UNINS="${LOCALAPPDATA}/Programs/BIMy.ai/BIMy for Revit/unins000.exe"

# --- 1. Revit lock check ----------------------------------------------------
# Revit maps RevitWallsPlugin.dll into its process. While Revit is running,
# Windows refuses to delete or overwrite the DLL — leaving a half-removed
# plugin and no easy way to finish the job.
if tasklist.exe //FI "IMAGENAME eq Revit.exe" 2>/dev/null | grep -qi "Revit.exe"; then
    echo "Revit is currently running — it locks the plugin DLL."
    read -r -p "Close Revit now (unsaved work in Revit will be lost)? [y/N] " answer
    if [[ "${answer,,}" == "y" || "${answer,,}" == "yes" ]]; then
        taskkill.exe //IM Revit.exe //T //F >/dev/null 2>&1 || true
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
    else
        echo "Aborted. Close Revit manually, then re-run ./uninstall.sh"
        exit 1
    fi
fi

# --- 2. Run the EXE uninstaller first, if present ---------------------------
# unins000.exe is the right tool for the EXE-installed side: it removes the
# files under %LocalAppData%\Programs\BIMy.ai\..., the Start Menu shortcut,
# and the HKCU Add/Remove Programs entry. We let it do its thing before we
# manually scrub the Revit Addins folders so there's no ARP ghost left over.
if [[ "${KEEP_EXE}" -eq 0 && -f "${EXE_UNINS}" ]]; then
    echo ">>> Running EXE uninstaller: ${EXE_UNINS}"
    # /VERYSILENT hides the wizard, /NORESTART suppresses reboot prompts,
    # /SUPPRESSMSGBOXES forces all dialogs to their default (Yes/OK) answer.
    "${EXE_UNINS}" //VERYSILENT //NORESTART //SUPPRESSMSGBOXES || true
    # Inno Setup's uninstaller returns almost immediately and finishes in the
    # background — give it a moment before we start deleting files it might
    # still be touching.
    sleep 2
elif [[ "${KEEP_EXE}" -eq 0 ]]; then
    echo ">>> No EXE install detected at ${EXE_UNINS} — skipping."
fi

# --- 3. Scrub the Revit Addins folders --------------------------------------
# Handles BOTH layouts (dev + exe) for every year under each root. Idempotent:
# if a file isn't there, we just skip it.
remove_in_year_dir() {
    local year_dir="$1"
    local year="$2"
    local any=0

    # Dev-install layout: flat files directly in <year>/
    for f in \
        "${year_dir}RevitWallsPlugin.dll" \
        "${year_dir}RevitWallsPlugin.addin" \
        "${year_dir}RevitWallsPlugin.log"
    do
        if [[ -f "${f}" ]]; then
            if rm -f "${f}" 2>/dev/null; then
                echo "  removed ${f}"
                any=1
            else
                echo "  WARN: could not remove ${f} (permission or still locked)"
            fi
        fi
    done

    # EXE-install layout: BIMy.addin + BIMy/ subfolder
    if [[ -f "${year_dir}BIMy.addin" ]]; then
        if rm -f "${year_dir}BIMy.addin" 2>/dev/null; then
            echo "  removed ${year_dir}BIMy.addin"
            any=1
        else
            echo "  WARN: could not remove ${year_dir}BIMy.addin"
        fi
    fi
    if [[ -d "${year_dir}BIMy" ]]; then
        if rm -rf "${year_dir}BIMy" 2>/dev/null; then
            echo "  removed ${year_dir}BIMy/"
            any=1
        else
            echo "  WARN: could not remove ${year_dir}BIMy/ (permission or files locked)"
        fi
    fi

    if [[ "${any}" -eq 0 ]]; then
        echo "  (nothing for Revit ${year})"
    fi
}

remove_from_root() {
    local root="$1"
    local label="$2"

    if [[ ! -d "${root}" ]]; then
        echo ">>> ${label}: ${root} does not exist — skipping."
        return 0
    fi

    echo ">>> ${label}: ${root}"

    if [[ "${ARG}" == "all" ]]; then
        shopt -s nullglob
        local found=0
        for year_dir in "${root}"/*/; do
            local year
            year="$(basename "${year_dir}")"
            # Revit year folders are always 4 digits. Skip anything else so we
            # don't touch third-party folders.
            [[ ! "${year}" =~ ^[0-9]{4}$ ]] && continue
            echo "Revit ${year}:"
            remove_in_year_dir "${year_dir}" "${year}"
            found=1
        done
        [[ "${found}" -eq 0 ]] && echo "  (no Revit year folders found)"
    else
        local year_dir="${root}/${ARG}/"
        if [[ -d "${year_dir}" ]]; then
            echo "Revit ${ARG}:"
            remove_in_year_dir "${year_dir}" "${ARG}"
        else
            echo "  (Revit ${ARG} folder not present)"
        fi
    fi
}

remove_from_root "${APPDATA_ROOT}"  "Per-user Addins"
remove_from_root "${PROGDATA_ROOT}" "Machine-wide Addins"

echo
echo "Done."
if [[ "${KEEP_EXE}" -eq 1 ]]; then
    echo "Note: --keep-exe was passed, so the EXE install under"
    echo "      ${LOCALAPPDATA}/Programs/BIMy.ai/BIMy for Revit/"
    echo "      and its Add/Remove Programs entry are still present."
fi
