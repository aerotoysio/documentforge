; Inno Setup script for DocumentForge Studio + service.
; Compiled by scripts/build-studio-installer.ps1 after the self-contained
; publishes stage files into dist\studio (client) and dist\service (dfdb).
;
; Component-selectable: install the Studio client, the dfdb service, or both.

#define AppName "DocumentForge Studio"
#ifndef AppVersion
  #define AppVersion "0.9.0"
#endif
#define Publisher "DocumentForge"
#define ExeName "DocumentForgeStudio.exe"
#define DataDir "C:\data\documentForge"

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

[Types]
Name: "full";   Description: "Full — Studio client + service"
Name: "client"; Description: "Studio client only"
Name: "server"; Description: "Service (dfdb) only"
Name: "custom"; Description: "Custom"; Flags: iscustom

[Components]
Name: "studio";  Description: "DocumentForge Studio (desktop client)"; Types: full client custom
Name: "service"; Description: "DocumentForge service (dfdb) — a local database engine you can connect to"; Types: full server custom

[Files]
Source: "..\dist\studio\*";  DestDir: "{app}";          Components: studio;  Flags: recursesubdirs ignoreversion createallsubdirs
Source: "..\dist\service\*"; DestDir: "{app}\service";  Components: service; Flags: recursesubdirs ignoreversion createallsubdirs

[Dirs]
; Default data directory for the service. Best-effort — the app/service also
; create it on first run. Never removed on uninstall (it holds your databases).
Name: "{#DataDir}"; Components: service; Flags: uninsneveruninstall

[Icons]
Name: "{group}\DocumentForge Studio"; Filename: "{app}\{#ExeName}"; Components: studio
Name: "{userdesktop}\DocumentForge Studio"; Filename: "{app}\{#ExeName}"; Components: studio; Tasks: desktopicon
Name: "{group}\Start DocumentForge Service"; Filename: "{app}\service\dfdb.exe"; \
    Parameters: "serve --port 5001 --data-dir ""{#DataDir}"""; Components: service
Name: "{group}\Uninstall DocumentForge Studio"; Filename: "{uninstallexe}"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Components: studio; Flags: unchecked

[Registry]
; Per-user .dfdb file association -> opens Studio (only if the client is installed).
Root: HKCU; Subkey: "Software\Classes\.dfdb"; ValueType: string; ValueName: ""; ValueData: "DocumentForge.Database"; Components: studio; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\DocumentForge.Database"; ValueType: string; ValueName: ""; ValueData: "DocumentForge Database"; Components: studio; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\DocumentForge.Database\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#ExeName},0"; Components: studio
Root: HKCU; Subkey: "Software\Classes\DocumentForge.Database\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#ExeName}"" ""%1"""; Components: studio

[Run]
Filename: "{app}\{#ExeName}"; Description: "Launch DocumentForge Studio"; Components: studio; Flags: nowait postinstall skipifsilent
Filename: "{app}\service\dfdb.exe"; Parameters: "serve --port 5001 --data-dir ""{#DataDir}"""; \
    Description: "Start the DocumentForge service now (port 5001)"; Components: service; Flags: nowait postinstall skipifsilent unchecked
