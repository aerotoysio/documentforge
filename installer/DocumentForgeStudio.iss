; Inno Setup script for DocumentForge Studio.
; Compiled by scripts/build-studio-installer.ps1 after the self-contained
; publishes have staged files into dist\studio (Studio + dfdb service).

#define AppName "DocumentForge Studio"
#ifndef AppVersion
  #define AppVersion "0.9.0"
#endif
#define Publisher "DocumentForge"
#define ExeName "DocumentForgeStudio.exe"
#define ServiceExe "service\dfdb.exe"

[Setup]
; Stable AppId so upgrades replace in place rather than installing side-by-side.
AppId={{7C4B4C2E-8F1A-4E2D-9B3A-DF5100BEEF01}}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#Publisher}
DefaultDirName={localappdata}\Programs\DocumentForge Studio
DefaultGroupName=DocumentForge Studio
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=..\dist\installer
OutputBaseFilename=DocumentForgeStudio-{#AppVersion}-setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\src\DocumentForge.Studio\Assets\studio.ico
UninstallDisplayIcon={app}\{#ExeName}
ChangesAssociations=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Files]
; Everything the self-contained publishes produced (Studio + service subfolder).
Source: "..\dist\studio\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion createallsubdirs

[Icons]
Name: "{group}\DocumentForge Studio"; Filename: "{app}\{#ExeName}"
Name: "{userdesktop}\DocumentForge Studio"; Filename: "{app}\{#ExeName}"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Registry]
; Per-user .dfdb file association -> opens Studio.
Root: HKCU; Subkey: "Software\Classes\.dfdb"; ValueType: string; ValueName: ""; ValueData: "DocumentForge.Database"; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\DocumentForge.Database"; ValueType: string; ValueName: ""; ValueData: "DocumentForge Database"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\DocumentForge.Database\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#ExeName},0"
Root: HKCU; Subkey: "Software\Classes\DocumentForge.Database\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#ExeName}"" ""%1"""

[Run]
Filename: "{app}\{#ExeName}"; Description: "Launch DocumentForge Studio"; Flags: nowait postinstall skipifsilent
