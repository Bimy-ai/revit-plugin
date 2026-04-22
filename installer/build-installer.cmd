@echo off
setlocal
rem Build Release DLLs and package them into Setup.exe via Inno Setup.
rem Usage: installer\build-installer.cmd

cd /d "%~dp0.."

echo === Building RevitWallsPlugin (Release) ===
dotnet build -c Release
if errorlevel 1 (
    echo.
    echo Build failed. Aborting.
    exit /b 1
)

set "ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"
if not exist "%ISCC%" set "ISCC=%ProgramFiles%\Inno Setup 6\ISCC.exe"

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
endlocal
