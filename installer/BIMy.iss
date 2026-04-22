; Inno Setup script — produces a Setup.exe installer for the BIMy Revit add-in.
; Build by running installer\build-installer.cmd (requires Inno Setup 6 installed).

; ─── CONFIGURE PUBLISHER INFO ─────────────────────────────────────────────────
; Update these before first release, then leave AppId alone forever (changing it
; breaks upgrades — new AppId installs side-by-side instead of replacing).

#define AppId          "{7E6A1F4B-9C2D-4A3E-B5F1-2D8C4E6A9B10}"
#define AppName        "BIMy for Revit"
#define AppVersion     "1.0.0"
#define AppPublisher   "Your Company Name"
#define AppURL         "https://example.com"
#define AppSupportURL  "https://example.com/support"
#define AppUpdatesURL  "https://example.com/downloads"
#define AppContact     "support@example.com"
#define AppCopyright   "Copyright (C) 2026 Your Company Name"

; Revit versions to register the add-in for. Drop entries if you don't want to
; support a particular year.
#define RevitYears     "2025,2026"

; Paths (relative to this .iss file)
#define SrcBin         "..\bin\Release"
#define AddinTemplate  "BIMy.addin.template"

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

DefaultDirName={autopf}\{#AppPublisher}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=auto
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\RevitWallsPlugin.dll

ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
PrivilegesRequired=admin
MinVersion=10.0

OutputDir=Output
OutputBaseFilename=BIMy-for-Revit-Setup-{#AppVersion}
Compression=lzma2/ultra
SolidCompression=yes
WizardStyle=modern

; Uncomment and configure once you have a code-signing certificate.
; Without a signed installer, SmartScreen will warn users on first run.
; SignTool=signtool
; SignedUninstaller=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "{#SrcBin}\RevitWallsPlugin.dll";       DestDir: "{app}"; Flags: ignoreversion
Source: "{#SrcBin}\RevitWallsPlugin.deps.json"; DestDir: "{app}"; Flags: ignoreversion
; PDB is optional — ship it for better crash diagnostics; remove this line to slim the installer.
Source: "{#SrcBin}\RevitWallsPlugin.pdb";       DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist
; Ship the template so [Code] can read and rewrite it during install.
Source: "{#AddinTemplate}";                     DestDir: "{tmp}"; Flags: deleteafterinstall

[UninstallDelete]
; Remove the per-version .addin manifests that were generated at install time.
Type: files; Name: "{commonappdata}\Autodesk\Revit\Addins\2025\BIMy.addin"
Type: files; Name: "{commonappdata}\Autodesk\Revit\Addins\2026\BIMy.addin"

[Code]
const
  TemplatePlaceholder = '__ASSEMBLY_PATH__';

function SplitYears(const Raw: string): TArrayOfString;
var
  S, Piece: string;
  P: Integer;
  Count: Integer;
begin
  S := Raw;
  Count := 0;
  SetArrayLength(Result, 0);
  while Length(S) > 0 do
  begin
    P := Pos(',', S);
    if P = 0 then
    begin
      Piece := Trim(S);
      S := '';
    end
    else
    begin
      Piece := Trim(Copy(S, 1, P - 1));
      S := Copy(S, P + 1, Length(S));
    end;
    if Length(Piece) > 0 then
    begin
      SetArrayLength(Result, Count + 1);
      Result[Count] := Piece;
      Count := Count + 1;
    end;
  end;
end;

function IsRevitRunning(): Boolean;
var
  ResultCode: Integer;
begin
  Result := False;
  // Exits 0 if Revit.exe is running, 1 otherwise.
  if Exec(ExpandConstant('{cmd}'), '/C tasklist /FI "IMAGENAME eq Revit.exe" | find /I "Revit.exe" >nul',
          '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    Result := (ResultCode = 0);
end;

function InitializeSetup(): Boolean;
begin
  Result := True;
  if IsRevitRunning() then
  begin
    if MsgBox('Revit is currently running. Please close Revit before continuing — otherwise the add-in files may be locked.' + #13#10 + #13#10 +
              'Continue anyway?', mbConfirmation, MB_YESNO) = IDNO then
      Result := False;
  end;
end;

procedure WriteAddinForYear(const Year, Template: string);
var
  Content, AssemblyPath, AddinDir, AddinPath: string;
begin
  AssemblyPath := ExpandConstant('{app}') + '\RevitWallsPlugin.dll';
  AddinDir := ExpandConstant('{commonappdata}') + '\Autodesk\Revit\Addins\' + Year;
  AddinPath := AddinDir + '\BIMy.addin';

  if not DirExists(AddinDir) then
  begin
    if not ForceDirectories(AddinDir) then
    begin
      Log('Could not create ' + AddinDir + ' — skipping Revit ' + Year);
      Exit;
    end;
  end;

  Content := Template;
  StringChangeEx(Content, TemplatePlaceholder, AssemblyPath, True);

  if not SaveStringToFile(AddinPath, Content, False) then
    Log('Failed to write ' + AddinPath);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Template: AnsiString;
  TemplateStr: string;
  TemplatePath: string;
  Years: TArrayOfString;
  I: Integer;
begin
  if CurStep <> ssPostInstall then Exit;

  TemplatePath := ExpandConstant('{tmp}\') + '{#AddinTemplate}';
  if not LoadStringFromFile(TemplatePath, Template) then
  begin
    MsgBox('Installer error: could not read ' + TemplatePath, mbError, MB_OK);
    Exit;
  end;
  TemplateStr := String(Template);

  Years := SplitYears('{#RevitYears}');
  for I := 0 to GetArrayLength(Years) - 1 do
    WriteAddinForYear(Years[I], TemplateStr);
end;
