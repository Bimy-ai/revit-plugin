; Inno Setup script — produces a Setup.exe that deploys the BIMy add-in into
; Revit's canonical Addins folder. Build by running build-installer.ps1 from the
; repository root (requires Inno Setup 6 installed).
;
; How this installer works (and why it "just works" for any Revit install):
;
;   1. No traditional "app directory" in Program Files. The plugin ships
;      directly into Revit's own Addins folder, which is the location Revit
;      scans on startup. No registry, no environment variables, no PATH.
;
;   2. Bundled deployment pattern (recommended by Autodesk):
;           ...\Addins\<year>\BIMy\BimyRevit.dll       <- payload
;           ...\Addins\<year>\BIMy.addin                      <- manifest
;      The .addin references the DLL with a RELATIVE path
;      (<Assembly>BIMy\BimyRevit.dll</Assembly>), so the layout is
;      self-contained and can be moved or copied without edits.
;
;   3. Installs per-user by default (%AppData%\Autodesk\Revit\Addins\<year>).
;      This requires no admin, no UAC, and won't fight antivirus tools that
;      flag writes to Program Files. If the user runs the installer elevated,
;      it also writes the machine-wide copy (%ProgramData%\...) so every
;      Windows account on the box picks the plugin up.
;
;   4. Revit versions are auto-detected by looking for Revit.exe under
;      C:\Program Files\Autodesk\Revit <year>\. Years 2022..2030 are probed
;      so the same Setup.exe keeps working as new Revit versions ship.

; ─── PUBLISHER INFO ───────────────────────────────────────────────────────────
; Update these before first release, then leave AppId alone (changing AppId
; breaks upgrades — new AppId installs side-by-side instead of replacing).

#define AppId          "{7E6A1F4B-9C2D-4A3E-B5F1-2D8C4E6A9B10}"
#define AppName        "BIMy for Revit"
; The release workflow stamps the version from the git tag with
;   ISCC /DAppVersion=1.2.3 installer\BIMy.iss
; so a tagged build can never disagree with the tag. Local builds fall through
; to the literal below, which dev-reinstall.ps1 -Bump increments in place.
#ifndef AppVersion
  #define AppVersion   "1.1.4"
#endif
#define AppPublisher   "BIMy.ai"
#define AppURL         "https://bimy.ai"
#define AppSupportURL  "https://bimy.ai/support"
#define AppUpdatesURL  "https://bimy.ai/downloads"
#define AppContact     "support@bimy.ai"
#define AppCopyright   "Copyright (C) 2026 BIMy.ai"

; Paths relative to this .iss file.
#define SrcBin         "..\bin\Release"
#define AddinTemplate  "BIMy.addin.template"

; Name of the plugin's subfolder inside <Addins>\<year>\. Must match the
; <Assembly> path prefix in BIMy.addin.template.
#define PluginFolder   "BIMy"
#define AddinFileName  "BIMy.addin"

[Setup]
AppId={{#AppId}}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL={#AppSupportURL}
AppUpdatesURL={#AppUpdatesURL}
AppContact={#AppContact}
AppCopyright={#AppCopyright}
VersionInfoVersion={#AppVersion}
VersionInfoCompany={#AppPublisher}
VersionInfoDescription={#AppName} Setup
VersionInfoProductName={#AppName}
VersionInfoCopyright={#AppCopyright}

; No dedicated install directory — files land directly in the Revit Addins
; folder(s). We still need an {app} so the uninstaller can register itself,
; but nothing user-visible lives there; keep it tidy in %LocalAppData%.
DefaultDirName={localappdata}\Programs\{#AppPublisher}\{#AppName}
DisableDirPage=yes
DisableProgramGroupPage=yes
CreateAppDir=yes
UninstallDisplayName={#AppName}

; Setup.exe file icon (what users see in Explorer / downloads folder) and the
; Add/Remove Programs icon are both driven by bimy-logo.ico. The ICO is
; generated from Resources\bimy-logo.png by Resources\generate-ico.ps1 —
; re-run that script whenever the brand PNG changes.
SetupIconFile=..\Resources\bimy-logo.ico
UninstallDisplayIcon={app}\bimy-logo.ico

; Start Menu group name. Inno Setup puts the [Icons] entries below under
; <user Start Menu>\Programs\<AppName>, so non-technical users can open Start,
; type "BIMy", and see "Uninstall BIMy for Revit" without needing to know
; about Control Panel / Add-Remove Programs.
DefaultGroupName={#AppName}

; Per-user install by default — no admin prompt. Users who double-click the
; Setup.exe just get a Revit-local install. If they Right-click → "Run as
; administrator", the installer also writes the machine-wide copy (handled
; in [Code] via IsAdminInstallMode).
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog

; x64compatible = native x64 + ARM64-Windows-running-x64-via-emulation. This
; is the most permissive identifier that still excludes 32-bit-only machines
; (which can't host Revit anyway). Plain "x64" gets substituted to "x64os" on
; Inno Setup 6.3+, which wrongly rejects ARM64 Windows with the unhelpful
; "This program does not support the version of Windows your computer is
; running" dialog.
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; No MinVersion — Inno Setup 6's own lower bound already excludes anything
; Revit 2025 wouldn't install on. Leaving it out means the "No Revit detected"
; message handles unsupported Windows versions gracefully instead of Setup
; aborting with a confusing generic error.

OutputDir=Output
OutputBaseFilename=BIMy-for-Revit-Setup-{#AppVersion}
Compression=lzma2/ultra
SolidCompression=yes
WizardStyle=modern

; Uncomment and configure once you have a code-signing certificate. Without a
; signed installer, SmartScreen will warn users on first launch.
; SignTool=signtool
; SignedUninstaller=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Icons]
; One Start Menu entry — just the uninstaller. The plugin itself has no
; user-launchable EXE (it runs inside Revit), so we don't need a "Launch BIMy"
; shortcut; only an easy path to uninstall.
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"

[Files]
; Stage everything into {tmp}\payload — [Code] copies it into each detected
; Revit year's Addins folder at the ssPostInstall step. Using DeleteAfterInstall
; keeps the {app} folder tiny (just the uninstaller).
Source: "{#SrcBin}\BimyRevit.dll";       DestDir: "{tmp}\payload"; Flags: deleteafterinstall
Source: "{#SrcBin}\BimyRevit.deps.json"; DestDir: "{tmp}\payload"; Flags: deleteafterinstall
Source: "{#SrcBin}\BimyRevit.pdb";       DestDir: "{tmp}\payload"; Flags: deleteafterinstall skipifsourcedoesntexist
Source: "{#AddinTemplate}";                     DestDir: "{tmp}";         Flags: deleteafterinstall

; Logo icon file ships to {app} so the UninstallDisplayIcon path resolves.
; Without this, Windows shows a generic icon in Add/Remove Programs even
; though Setup.exe itself looks correct.
Source: "..\Resources\bimy-logo.ico";           DestDir: "{app}"; Flags: ignoreversion

[Code]
const
  // Probe this inclusive range for installed Revit versions. Bump the upper
  // bound when new Revit years ship — no other code changes needed.
  FirstRevitYear = 2022;
  LastRevitYear  = 2030;

// Files and folders we drop in each <Addins>\<year>. Kept in one place so the
// uninstaller can find and remove them symmetrically.
function AddinFile(): string;     begin Result := '{#AddinFileName}'; end;
function PluginFolderName(): string; begin Result := '{#PluginFolder}'; end;

// Revit year is "installed" if Revit.exe exists under its canonical Program
// Files location. Registry keys move around between Revit releases, so this
// file-based probe is the most stable signal.
function RevitYearInstalled(const Year: string): Boolean;
begin
  Result := FileExists(ExpandConstant('{pf}') + '\Autodesk\Revit ' + Year + '\Revit.exe');
end;

function DetectInstalledRevitYears(): TArrayOfString;
var
  I, Count: Integer;
  YearStr: string;
begin
  Count := 0;
  SetArrayLength(Result, 0);
  for I := FirstRevitYear to LastRevitYear do
  begin
    YearStr := IntToStr(I);
    if RevitYearInstalled(YearStr) then
    begin
      SetArrayLength(Result, Count + 1);
      Result[Count] := YearStr;
      Count := Count + 1;
    end;
  end;
end;

function UserAddinsRoot(): string;
begin
  Result := ExpandConstant('{userappdata}') + '\Autodesk\Revit\Addins';
end;

function MachineAddinsRoot(): string;
begin
  Result := ExpandConstant('{commonappdata}') + '\Autodesk\Revit\Addins';
end;

// Returns True if installer is running elevated (admin). Revit's machine-wide
// Addins folder under %ProgramData% requires admin to write to.
function CanWriteMachineAddins(): Boolean;
begin
  Result := IsAdminInstallMode();
end;

function IsRevitRunning(): Boolean;
var
  ResultCode: Integer;
begin
  Result := False;
  // tasklist /FI ... then `find` — exit 0 means a match, 1 means no match.
  if Exec(ExpandConstant('{cmd}'),
          '/C tasklist /FI "IMAGENAME eq Revit.exe" | find /I "Revit.exe" >nul',
          '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    Result := (ResultCode = 0);
end;

// EVERY message box below is a SuppressibleMsgBox, never a plain MsgBox.
// /SUPPRESSMSGBOXES does not touch MsgBox() calls made from [Code] — only
// Setup's own built-in prompts — so a plain MsgBox puts a modal window on an
// invisible /VERYSILENT install and waits for a click that will never come. The
// install completes and then hangs forever. That breaks unattended deployment
// (an IT push, a CI job, dev-reinstall.ps1). The final argument is the answer to
// assume when suppressed.
function InitializeSetup(): Boolean;
begin
  Result := True;

  // No Revit on this machine → nothing for us to install. Bail out BEFORE the
  // uninstaller entry gets registered (which would happen once CurStepChanged
  // fires), so the user isn't left with a ghost "BIMy for Revit" entry in
  // Add/Remove Programs.
  if GetArrayLength(DetectInstalledRevitYears()) = 0 then
  begin
    SuppressibleMsgBox('No Revit installations were detected on this machine ' +
           '(looked for C:\Program Files\Autodesk\Revit <year>\Revit.exe for years ' +
           IntToStr(FirstRevitYear) + '–' + IntToStr(LastRevitYear) + ').' + #13#10 + #13#10 +
           'Install Revit first, then re-run this Setup.',
           mbInformation, MB_OK, IDOK);
    Result := False;
    Exit;
  end;

  if IsRevitRunning() then
  begin
    // Suppressed answer is IDYES: a silent install was launched by something
    // that has already decided (dev-reinstall.ps1 closes Revit itself), and
    // stopping there would strand it.
    if SuppressibleMsgBox('Revit is currently running. Please close Revit before continuing — otherwise the add-in files may be locked and the install can fail silently.' + #13#10 + #13#10 +
              'Continue anyway?', mbConfirmation, MB_YESNO, IDYES) = IDNO then
      Result := False;
  end;
end;

// Copy every staged file into <destBase>\<PluginFolder>\. Returns True on
// success, False on the first failure (so we can log a clear message).
function CopyPayloadInto(const DestBase: string): Boolean;
var
  Src, Dst: string;
begin
  Result := False;
  Src := ExpandConstant('{tmp}\payload');
  Dst := DestBase + '\' + PluginFolderName();

  if not ForceDirectories(Dst) then Exit;

  // Builds before the rename shipped the payload as RevitWallsPlugin.*. Setup
  // installs over them rather than uninstalling first, so those files would
  // otherwise sit in this folder forever — unreferenced by any manifest, but
  // indistinguishable from a live plug-in to anyone who looks.
  DeleteFile(Dst + '\RevitWallsPlugin.dll');
  DeleteFile(Dst + '\RevitWallsPlugin.deps.json');
  DeleteFile(Dst + '\RevitWallsPlugin.pdb');

  if not CopyFile(Src + '\BimyRevit.dll', Dst + '\BimyRevit.dll', False) then Exit;

  // deps.json is optional but shipped — ignore failure, the plugin still loads.
  CopyFile(Src + '\BimyRevit.deps.json', Dst + '\BimyRevit.deps.json', False);

  // PDB is optional and best-effort (isn't always present in the bin folder).
  if FileExists(Src + '\BimyRevit.pdb') then
    CopyFile(Src + '\BimyRevit.pdb', Dst + '\BimyRevit.pdb', False);

  Result := True;
end;

// Write <AddinsRoot>\<year>\<BIMy.addin> and copy DLLs into <year>\BIMy\.
// Returns True on success.
function DeployToAddinsRoot(const AddinsRoot, Year, ManifestXml: string): Boolean;
var
  YearDir, AddinPath: string;
begin
  Result := False;
  YearDir := AddinsRoot + '\' + Year;
  AddinPath := YearDir + '\' + AddinFile();

  if not ForceDirectories(YearDir) then
  begin
    Log('Could not create ' + YearDir);
    Exit;
  end;

  if not CopyPayloadInto(YearDir) then
  begin
    Log('Could not copy payload into ' + YearDir + '\' + PluginFolderName());
    Exit;
  end;

  if not SaveStringToFile(AddinPath, ManifestXml, False) then
  begin
    Log('Failed to write ' + AddinPath);
    Exit;
  end;

  Log('Installed for Revit ' + Year + ' at ' + AddinPath);
  Result := True;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  TemplateBytes: AnsiString;
  ManifestXml: string;
  TemplatePath: string;
  Years: TArrayOfString;
  Installed: TArrayOfString;
  Y: string;
  I, Count: Integer;
  Summary: string;
begin
  if CurStep <> ssPostInstall then Exit;

  TemplatePath := ExpandConstant('{tmp}\{#AddinTemplate}');
  if not LoadStringFromFile(TemplatePath, TemplateBytes) then
  begin
    SuppressibleMsgBox('Installer error: could not read ' + TemplatePath, mbError, MB_OK, IDOK);
    Exit;
  end;
  ManifestXml := String(TemplateBytes);

  // Safe to call — InitializeSetup already rejected empty results, so Years
  // is guaranteed to have at least one entry here.
  Years := DetectInstalledRevitYears();

  SetArrayLength(Installed, 0);
  Count := 0;

  for I := 0 to GetArrayLength(Years) - 1 do
  begin
    Y := Years[I];

    // Per-user install always succeeds (no privileges needed).
    if DeployToAddinsRoot(UserAddinsRoot(), Y, ManifestXml) then
    begin
      SetArrayLength(Installed, Count + 1);
      Installed[Count] := Y + ' (per-user)';
      Count := Count + 1;
    end;

    // Machine-wide copy only when running elevated. Revit scans both locations
    // so this is additive, not a replacement.
    if CanWriteMachineAddins() then
    begin
      if DeployToAddinsRoot(MachineAddinsRoot(), Y, ManifestXml) then
      begin
        SetArrayLength(Installed, Count + 1);
        Installed[Count] := Y + ' (all users)';
        Count := Count + 1;
      end;
    end;
  end;

  if GetArrayLength(Installed) = 0 then
  begin
    SuppressibleMsgBox('Installation failed — could not write the add-in files to any Revit Addins folder. ' +
           'See the setup log for details.', mbError, MB_OK, IDOK);
    Exit;
  end;

  Summary := '{#AppName} was installed for Revit:' + #13#10#13#10;
  for I := 0 to GetArrayLength(Installed) - 1 do
    Summary := Summary + '    • Revit ' + Installed[I] + #13#10;
  Summary := Summary + #13#10 +
             'Restart Revit (if it''s open) to see the "BIMy" tab in the ribbon.';
  if not CanWriteMachineAddins() then
    Summary := Summary + #13#10#13#10 +
               'Tip: run this installer as administrator to also install for other Windows accounts on this machine.';

  SuppressibleMsgBox(Summary, mbInformation, MB_OK, IDOK);
end;

// ─── Uninstall ────────────────────────────────────────────────────────────────
// Symmetric cleanup: remove <Addins>\<year>\BIMy.addin and the BIMy\
// subfolder from both per-user and per-machine locations for every year we
// might have installed into.

// Refuse to start uninstalling while Revit is running — the DLL is mapped
// into Revit's process and Windows will deny the delete, leaving an orphaned
// BIMy\ folder in %AppData%\Autodesk\Revit\Addins\<year>\ with no easy way
// to clean it up (the uninstaller itself is gone by then).
function InitializeUninstall(): Boolean;
begin
  Result := True;
  if IsRevitRunning() then
  begin
    SuppressibleMsgBox('Revit is currently running. Close Revit first, then run the uninstaller again — otherwise the plugin DLL is locked and won''t be removed cleanly.',
           mbInformation, MB_OK, IDOK);
    Result := False;
  end;
end;

procedure RemoveFromAddinsRoot(const AddinsRoot: string);
var
  I: Integer;
  YearDir, AddinPath, PluginDir: string;
begin
  for I := FirstRevitYear to LastRevitYear do
  begin
    YearDir   := AddinsRoot + '\' + IntToStr(I);
    AddinPath := YearDir + '\' + AddinFile();
    PluginDir := YearDir + '\' + PluginFolderName();

    if FileExists(AddinPath) then
      DeleteFile(AddinPath);
    if DirExists(PluginDir) then
      DelTree(PluginDir, True, True, True);
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep <> usUninstall then Exit;
  RemoveFromAddinsRoot(UserAddinsRoot());
  if CanWriteMachineAddins() then
    RemoveFromAddinsRoot(MachineAddinsRoot());
end;
