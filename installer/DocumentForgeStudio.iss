; Inno Setup script for DocumentForge Studio + service.
; Compiled by scripts/build-studio-installer.ps1 after the self-contained
; publishes stage files into dist\studio (client) and dist\service (dfdb).
;
; Component-selectable: install the Studio client, the dfdb service, or both.

#define AppName "DocumentForge Studio"
#ifndef AppVersion
  #define AppVersion "0.12.0"
#endif
#define Publisher "DocumentForge"
#define ExeName "DocumentForgeStudio.exe"
#define DataDir "C:\data\documentforge"
#define DefaultPort "4300"

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
; The DocumentForge data folder: databases (*.dfdb), WAL, backups, and the
; installer's port.txt / service-key.txt. Chosen on the wizard's data-folder
; page. Never removed on uninstall (it holds your databases).
Name: "{code:GetDataDir}"; Flags: uninsneveruninstall

[Icons]
Name: "{group}\DocumentForge Studio"; Filename: "{app}\{#ExeName}"; Components: studio
Name: "{userdesktop}\DocumentForge Studio"; Filename: "{app}\{#ExeName}"; Components: studio; Tasks: desktopicon
Name: "{group}\Start DocumentForge Service"; Filename: "{app}\service\dfdb.exe"; \
    Parameters: "serve --port {code:GetPort} --data-dir ""{code:GetDataDir}"" --api-key ""{code:GetApiKey}"""; Components: service
Name: "{group}\Uninstall DocumentForge Studio"; Filename: "{uninstallexe}"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Components: studio; Flags: unchecked
Name: "startupservice"; Description: "Install as a Windows service (auto-start on boot, managed in services.msc — needs admin approval)"; GroupDescription: "Service:"; Components: service; Flags: unchecked

[Registry]
; Per-user .dfdb file association -> opens Studio (only if the client is installed).
Root: HKCU; Subkey: "Software\Classes\.dfdb"; ValueType: string; ValueName: ""; ValueData: "DocumentForge.Database"; Components: studio; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\DocumentForge.Database"; ValueType: string; ValueName: ""; ValueData: "DocumentForge Database"; Components: studio; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\DocumentForge.Database\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#ExeName},0"; Components: studio
Root: HKCU; Subkey: "Software\Classes\DocumentForge.Database\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#ExeName}"" ""%1"""; Components: studio

[Run]
Filename: "{app}\{#ExeName}"; Description: "Launch DocumentForge Studio"; Components: studio; Flags: nowait postinstall skipifsilent
Filename: "{app}\service\dfdb.exe"; Parameters: "serve --port {code:GetPort} --data-dir ""{code:GetDataDir}"" --api-key ""{code:GetApiKey}"""; \
    Description: "Start the DocumentForge service now"; Components: service; Flags: nowait postinstall skipifsilent unchecked

[UninstallDelete]
; Files written by [Code] — the uninstaller doesn't track those on its own.
; port.txt / service-key.txt live in the data folder, which survives
; uninstall by design; the {app} entries cover pre-0.10.1 locations.
Type: files; Name: "{app}\datadir.txt"
Type: files; Name: "{app}\port.txt"
Type: files; Name: "{app}\service-key.txt"
Type: files; Name: "{app}\service-installed.txt"

[Code]
var
  PortPage: TInputQueryWizardPage;
  DataDirPage: TInputDirWizardPage;
  ApiKeyValue: String;
  ServiceWasRunning: Boolean;

// The Windows service `dfdb service install` registers (ServiceCommand.cs).
const
  ServiceName = 'DocumentForge';

// sc query exits 0 when the service exists (any state), 1060 when it doesn't.
function ServiceExists(): Boolean;
var
  RC: Integer;
begin
  Result := Exec(ExpandConstant('{sys}\sc.exe'), 'query "' + ServiceName + '"', '',
                 SW_HIDE, ewWaitUntilTerminated, RC) and (RC = 0);
end;

// Piping through findstr gives us the state without parsing output ourselves:
// exit 0 = the STATE line says RUNNING (or a pending transition to/from it).
function ServiceRunning(): Boolean;
var
  RC: Integer;
begin
  Result := Exec(ExpandConstant('{cmd}'),
                 '/c sc query "' + ServiceName + '" | findstr /i /c:"RUNNING"', '',
                 SW_HIDE, ewWaitUntilTerminated, RC) and (RC = 0);
end;

// Upgrades used to fail mid-copy because dfdb.exe (the running service or a
// Start-Menu instance) and Studio itself held locks on files in {app}. Stop
// everything BEFORE the file copy; the service is restarted post-install if
// it was running.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  RC, Tries: Integer;
begin
  Result := '';
  ServiceWasRunning := False;

  // A running Studio locks the client files. Same-user process, no elevation
  // needed; ignore failures (it may simply not be running).
  Exec(ExpandConstant('{cmd}'), '/c taskkill /f /im DocumentForgeStudio.exe', '',
       SW_HIDE, ewWaitUntilTerminated, RC);

  if ServiceExists() and ServiceRunning() then
  begin
    ServiceWasRunning := True;
    // The installer is per-user; stopping a service needs admin, so elevate
    // just this one command (one UAC prompt).
    ShellExec('runas', ExpandConstant('{sys}\sc.exe'), 'stop "' + ServiceName + '"', '',
              SW_HIDE, ewWaitUntilTerminated, RC);
    // Wait for the SCM to actually finish stopping (up to ~15 s) so the
    // file copy doesn't race the shutdown.
    Tries := 0;
    while ServiceRunning() and (Tries < 30) do
    begin
      Sleep(500);
      Tries := Tries + 1;
    end;
  end;

  // Any manually-started dfdb (Start-Menu shortcut) also locks {app}\service.
  // Won't touch the service's own process (different user, access denied) —
  // that one was handled above.
  Exec(ExpandConstant('{cmd}'), '/c taskkill /f /im dfdb.exe', '',
       SW_HIDE, ewWaitUntilTerminated, RC);
end;

procedure InitializeWizard;
begin
  PortPage := CreateInputQueryPage(wpSelectComponents,
    'DocumentForge port',
    'Which TCP port should the DocumentForge service use?',
    'The service and Studio''s default connection use this port. ' +
    '4300 is the DocumentForge standard; change it only if 4300 is already in use.');
  PortPage.Add('Port:', False);
  PortPage.Values[0] := '{#DefaultPort}';

  DataDirPage := CreateInputDirPage(PortPage.ID,
    'DocumentForge data folder',
    'Where should DocumentForge keep its databases?',
    'Databases (*.dfdb), the write-ahead log, backups and the service''s ' +
    'port/key files all live in this folder. It is never removed on uninstall.',
    False, '');
  DataDirPage.Add('');
  DataDirPage.Values[0] := '{#DataDir}';
end;

function GetPort(Param: String): String;
begin
  Result := Trim(PortPage.Values[0]);
  if Result = '' then
    Result := '{#DefaultPort}';
end;

function GetDataDir(Param: String): String;
begin
  Result := RemoveBackslashUnlessRoot(Trim(DataDirPage.Values[0]));
  if Result = '' then
    Result := '{#DataDir}';
end;

// The API key the bundled service runs with (dfdb refuses to start without
// auth — deny by default). Reused from service-key.txt on upgrades so a new
// installer run doesn't orphan an already-registered service; generated
// fresh on first install. Studio reads the same file on startup to seed its
// local connection, so the key is never typed by hand.
function GetApiKey(Param: String): String;
var
  KeyFile: String;
  Existing: AnsiString;
begin
  if ApiKeyValue = '' then
  begin
    KeyFile := GetDataDir('') + '\service-key.txt';
    if not FileExists(KeyFile) then
      KeyFile := ExpandConstant('{app}\service-key.txt'); // pre-0.10.1 location
    if FileExists(KeyFile) and LoadStringFromFile(KeyFile, Existing) and (Trim(String(Existing)) <> '') then
      ApiKeyValue := Trim(String(Existing))
    else
      // Inno's script PRNG has no Randomize, so don't trust it alone:
      // hash install-time + machine/user identity + PRNG output into a
      // 64-hex key that's unique per machine and install.
      ApiKeyValue := GetSHA256OfUnicodeString(
        GetDateTimeString('yyyy/mm/dd hh:nn:ss', '-', ':') + '|' +
        ExpandConstant('{computername}|{username}') + '|' +
        IntToStr(Random(2147483647)) + '|' + IntToStr(Random(2147483647)));
  end;
  Result := ApiKeyValue;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  Port: Integer;
begin
  Result := True;
  if CurPageID = PortPage.ID then
  begin
    Port := StrToIntDef(Trim(PortPage.Values[0]), -1);
    if (Port < 1) or (Port > 65535) then
    begin
      MsgBox('Please enter a valid port number (1-65535).', mbError, MB_OK);
      Result := False;
    end;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Exe, Params: String;
  ResultCode: Integer;
begin
  if CurStep <> ssPostInstall then
    Exit;

  // The data folder holds the operator-facing config files: port.txt and
  // service-key.txt. A one-line pointer (datadir.txt) next to the app tells
  // Studio where that folder is, since the user can move it in the wizard.
  ForceDirectories(GetDataDir(''));
  SaveStringToFile(ExpandConstant('{app}\datadir.txt'), GetDataDir(''), False);
  SaveStringToFile(GetDataDir('') + '\port.txt', GetPort(''), False);

  // Persist the service API key next to port.txt whenever the service ships:
  // Studio reads it on startup to seed an authenticated local connection,
  // and it's the operator's reference copy for manual `dfdb service install`.
  if WizardIsComponentSelected('service') then
    SaveStringToFile(GetDataDir('') + '\service-key.txt', GetApiKey(''), False);

  // Optional: register a real Windows service (dfdb service install, which also
  // starts it). Elevated via UAC because the installer itself is per-user.
  if WizardIsComponentSelected('service') and WizardIsTaskSelected('startupservice') then
  begin
    Exe := ExpandConstant('{app}\service\dfdb.exe');
    Params := 'service install --port ' + GetPort('') + ' --data-dir "' + GetDataDir('') + '"'
              + ' --api-key "' + GetApiKey('') + '"';
    if not ShellExec('runas', Exe, Params, '', SW_HIDE, ewWaitUntilTerminated, ResultCode)
       or (ResultCode <> 0) then
      MsgBox('The Windows service could not be installed (you may have declined the admin prompt). ' +
             'You can install it later from an elevated prompt with:  dfdb service install --api-key <key> ' +
             '(the key is saved in service-key.txt in the data folder), ' +
             'or run it manually from the Start Menu shortcut.', mbInformation, MB_OK)
    else
      // Marker so uninstall knows to remove the service.
      SaveStringToFile(ExpandConstant('{app}\service-installed.txt'), 'DocumentForge', False);
  end;

  // If PrepareToInstall stopped a running service for the upgrade, bring it
  // back with the freshly-installed binaries. Skipped when the wizard just
  // (re)ran `dfdb service install` above — that already starts it.
  if ServiceWasRunning
     and not (WizardIsComponentSelected('service') and WizardIsTaskSelected('startupservice')) then
  begin
    if not ShellExec('runas', ExpandConstant('{sys}\sc.exe'), 'start "' + ServiceName + '"', '',
                     SW_HIDE, ewWaitUntilTerminated, ResultCode)
       or (ResultCode <> 0) then
      MsgBox('The DocumentForge service was stopped for the upgrade but could not be restarted ' +
             '(you may have declined the admin prompt). Start it from services.msc or an elevated ' +
             'prompt with:  sc start ' + ServiceName, mbInformation, MB_OK);
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
begin
  // Remove the Windows service before the files go (only if we installed it).
  if (CurUninstallStep = usUninstall) and FileExists(ExpandConstant('{app}\service-installed.txt')) then
    ShellExec('runas', ExpandConstant('{app}\service\dfdb.exe'), 'service uninstall', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;
