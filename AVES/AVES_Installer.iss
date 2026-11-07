; ============================================
; AVES Revit Plugin - Inno Setup Script
; ============================================

; Build output of each Revit configuration, relative to this script. Each Revit
; version gets the DLLs built against its own API, so the two are never mixed.
#define BinR25 SourcePath + "bin\x64\Release R25"
#define BinR26 SourcePath + "bin\x64\Release R26"
#define AddinsDir "{commonappdata}\Autodesk\Revit\Addins"

[Setup]
; AppId is a unique GUID that identifies this application across ALL versions.
; This is what allows the installer to detect a previous installation and upgrade it.
; NEVER change this GUID — if you do, users will get duplicate entries in Add/Remove Programs.
AppId={{A1B2C3D4-E5F6-7890-ABCD-EF1234567890}

AppName=AVES Revit Plugin
AppVersion=1.1.0
AppPublisher=Your Company Name
AppPublisherURL=https://github.com/yourcompany/AVES

AppMutex=AVESRevitPlugin_Mutex

DefaultDirName={commonappdata}\Autodesk\Revit\Addins
CreateAppDir=no

OutputDir=installer_output
OutputBaseFilename=AVES_Setup_v1.1.0

Compression=lzma
SolidCompression=yes
WizardStyle=modern

DisableDirPage=yes
DisableProgramGroupPage=yes
CloseApplications=yes
CloseApplicationsFilter=*.exe
RestartApplications=no
UsePreviousAppDir=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Messages]
WelcomeLabel2=This will install (or update) the AVES Plugin for Autodesk Revit on your computer.%n%nIf a previous version is detected, it will be automatically replaced.%n%nPlease close Revit before continuing.

[Files]
; Revit 2025
Source: "{#BinR25}\AVES.dll";                        DestDir: "{#AddinsDir}\2025"; Check: IsRevitInstalled('2025'); Flags: ignoreversion uninsrestartdelete
Source: "{#BinR25}\AVES.deps.json";                  DestDir: "{#AddinsDir}\2025"; Check: IsRevitInstalled('2025'); Flags: ignoreversion uninsrestartdelete
Source: "{#BinR25}\AVES.runtimeconfig.json";         DestDir: "{#AddinsDir}\2025"; Check: IsRevitInstalled('2025'); Flags: ignoreversion uninsrestartdelete
Source: "{#BinR25}\Nice3point.Revit.Extensions.dll"; DestDir: "{#AddinsDir}\2025"; Check: IsRevitInstalled('2025'); Flags: ignoreversion uninsrestartdelete
Source: "{#BinR25}\Nice3point.Revit.Toolkit.dll";    DestDir: "{#AddinsDir}\2025"; Check: IsRevitInstalled('2025'); Flags: ignoreversion uninsrestartdelete
Source: "{#BinR25}\SharpGLTF.Core.dll";                      DestDir: "{#AddinsDir}\2025"; Check: IsRevitInstalled('2025'); Flags: ignoreversion uninsrestartdelete
Source: "{#BinR25}\SharpGLTF.Runtime.dll";                   DestDir: "{#AddinsDir}\2025"; Check: IsRevitInstalled('2025'); Flags: ignoreversion uninsrestartdelete
Source: "{#BinR25}\SharpGLTF.Toolkit.dll";                   DestDir: "{#AddinsDir}\2025"; Check: IsRevitInstalled('2025'); Flags: ignoreversion uninsrestartdelete
Source: "{#SourcePath}AVES.addin";                   DestDir: "{#AddinsDir}\2025"; Check: IsRevitInstalled('2025'); Flags: ignoreversion uninsrestartdelete

; Revit 2026
Source: "{#BinR26}\AVES.dll";                        DestDir: "{#AddinsDir}\2026"; Check: IsRevitInstalled('2026'); Flags: ignoreversion uninsrestartdelete
Source: "{#BinR26}\AVES.deps.json";                  DestDir: "{#AddinsDir}\2026"; Check: IsRevitInstalled('2026'); Flags: ignoreversion uninsrestartdelete
Source: "{#BinR26}\AVES.runtimeconfig.json";         DestDir: "{#AddinsDir}\2026"; Check: IsRevitInstalled('2026'); Flags: ignoreversion uninsrestartdelete
Source: "{#BinR26}\Nice3point.Revit.Extensions.dll"; DestDir: "{#AddinsDir}\2026"; Check: IsRevitInstalled('2026'); Flags: ignoreversion uninsrestartdelete
Source: "{#BinR26}\Nice3point.Revit.Toolkit.dll";    DestDir: "{#AddinsDir}\2026"; Check: IsRevitInstalled('2026'); Flags: ignoreversion uninsrestartdelete
Source: "{#BinR26}\SharpGLTF.Core.dll";                      DestDir: "{#AddinsDir}\2026"; Check: IsRevitInstalled('2026'); Flags: ignoreversion uninsrestartdelete
Source: "{#BinR26}\SharpGLTF.Runtime.dll";                   DestDir: "{#AddinsDir}\2026"; Check: IsRevitInstalled('2026'); Flags: ignoreversion uninsrestartdelete
Source: "{#BinR26}\SharpGLTF.Toolkit.dll";                   DestDir: "{#AddinsDir}\2026"; Check: IsRevitInstalled('2026'); Flags: ignoreversion uninsrestartdelete
Source: "{#SourcePath}AVES.addin";                   DestDir: "{#AddinsDir}\2026"; Check: IsRevitInstalled('2026'); Flags: ignoreversion uninsrestartdelete

[Code]

// ── Per-version helpers ───────────────────────────────────────────────────────
// This installer carries a build for Revit 2025 and one for Revit 2026. A version
// counts as installed when its ProgramData add-ins folder exists, and each one is
// installed independently, so a machine with both gets both. Also used as the
// Check: for every [Files] entry above.
function AddinsFolder(Year: String): String;
begin
  Result := ExpandConstant('{commonappdata}') + '\Autodesk\Revit\Addins\' + Year;
end;

function IsRevitInstalled(Year: String): Boolean;
begin
  Result := DirExists(AddinsFolder(Year));
end;

// ── Check whether a previous version is already installed ─────────────────────
function PreviousInstallDetected(Year: String): Boolean;
begin
  Result := FileExists(AddinsFolder(Year) + '\AVES.dll');
end;

// ── Clean up stale AppData leftovers from older installations ─────────────────
// This handles cases where AVES.addin was previously installed to the user
// AppData folder instead of ProgramData, which causes Revit to fail loading
// the DLL because the two files end up in different locations.
procedure CleanupAppDataLeftovers(Year: String);
var
  AppDataAddin: String;
  AppDataFolder: String;
begin
  // Remove stale AVES.addin from user AppData if it exists
  AppDataAddin := ExpandConstant('{userappdata}') + '\Autodesk\Revit\Addins\' + Year + '\AVES.addin';
  if FileExists(AppDataAddin) then
    DeleteFile(AppDataAddin);

  // Remove stale AVES folder from user AppData if it exists
  AppDataFolder := ExpandConstant('{userappdata}') + '\Autodesk\Revit\Addins\' + Year + '\AVES';
  if DirExists(AppDataFolder) then
    DelTree(AppDataFolder, True, True, True);
end;

// ── Entry point ───────────────────────────────────────────────────────────────
function InitializeSetup(): Boolean;
var
  Targets: String;
  Updating: Boolean;
  WelcomeMsg: String;
begin
  // Abort if neither supported Revit version is installed
  if not IsRevitInstalled('2025') and not IsRevitInstalled('2026') then
  begin
    MsgBox(
      'No supported Revit installation (2025 or 2026) was found on this computer.' + #13#10 +
      'Please install Autodesk Revit 2025 or 2026 before running this installer.',
      mbError, MB_OK);
    Result := False;
    Exit;
  end;

  Targets := '';
  Updating := False;

  if IsRevitInstalled('2025') then
  begin
    CleanupAppDataLeftovers('2025');
    Targets := Targets + 'Revit 2025:  ' + AddinsFolder('2025') + #13#10;
    if PreviousInstallDetected('2025') then Updating := True;
  end;

  if IsRevitInstalled('2026') then
  begin
    CleanupAppDataLeftovers('2026');
    Targets := Targets + 'Revit 2026:  ' + AddinsFolder('2026') + #13#10;
    if PreviousInstallDetected('2026') then Updating := True;
  end;

  // Tailor the welcome message depending on fresh install vs update
  if Updating then
    WelcomeMsg :=
      'A previous version of AVES was detected.' + #13#10 +
      'It will be automatically updated - no manual uninstall required.' + #13#10 + #13#10 +
      'Install path:' + #13#10 + Targets
  else
    WelcomeMsg :=
      'AVES will be installed to:' + #13#10 + Targets;

  MsgBox(WelcomeMsg, mbInformation, MB_OK);
  Result := True;
end;
