@echo off
setlocal
rem Build Release DLLs and package them into a Setup.exe via Inno Setup.
rem The resulting installer deploys the plugin directly into Revit's Addins
rem folder and auto-detects installed Revit versions — see BIMy.iss for the
rem full rationale.
rem
rem Usage: installer\build-installer.cmd

cd /d "%~dp0.."

rem Auto-detect a Revit install with the actual API DLLs present. The csproj
rem defaults to 2025 but that folder is sometimes a shell with no RevitAPI.dll
rem (e.g. on an upgraded machine). Probe newest-first so we build against the
rem most modern API available; the installer itself deploys to every detected
rem year at install time. Override by setting REVIT_VERSION before invoking.
if not defined REVIT_ROOT set "REVIT_ROOT=%ProgramFiles%\Autodesk"
if not defined REVIT_VERSION (
    for %%Y in (2030 2029 2028 2027 2026 2025 2024 2023 2022) do (
        if not defined REVIT_VERSION if exist "%REVIT_ROOT%\Revit %%Y\RevitAPI.dll" set "REVIT_VERSION=%%Y"
    )
)

if not defined REVIT_VERSION (
    echo.
    echo No Revit install with RevitAPI.dll was found under "%REVIT_ROOT%".
    echo Install Revit ^(any year 2022-2030^) and re-run this script, or set
    echo REVIT_VERSION=^<year^> if the DLLs live in a non-standard location.
    exit /b 1
)

echo === Building RevitWallsPlugin (Release) - targeting Revit %REVIT_VERSION% ===
rem --disable-build-servers keeps MSBuild / Roslyn / Razor servers from
rem outliving this script. Without it they inherit the console handle and
rem the parent prompt hangs waiting for them after the build finishes.
dotnet build -c Release /p:RevitVersion=%REVIT_VERSION% --disable-build-servers
if errorlevel 1 (
    echo.
    echo Build failed. Aborting.
    exit /b 1
)

set "ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"
if not exist "%ISCC%" set "ISCC=%ProgramFiles%\Inno Setup 6\ISCC.exe"
if not exist "%ISCC%" set "ISCC=%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe"

if not exist "%ISCC%" (
    echo.
    echo Inno Setup 6 was not found. Install it from:
    echo     https://jrsoftware.org/isdl.php
    echo and re-run this script.
    exit /b 1
)

echo === Packaging installer ===
"%ISCC%" "installer\BIMy.iss"
if errorlevel 1 (
    echo.
    echo ISCC failed.
    exit /b 1
)

echo.
echo Done. Installer in installer\Output\
echo Double-click the Setup.exe to install for the current user (no admin).
echo Right-click -^> "Run as administrator" to also install machine-wide.
endlocal
