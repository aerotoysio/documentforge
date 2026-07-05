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
; Default data directory for the service. Best-effort — the app/service also
; create it on first run. Never removed on uninstall (it holds your databases).
Name: "{#DataDir}"; Components: service; Flags: uninsneveruninstall

[Icons]
Name: "{group}\DocumentForge Studio"; Filename: "{app}\{#ExeName}"; Components: studio
Name: "{userdesktop}\DocumentForge Studio"; Filename: "{app}\{#ExeName}"; Components: studio; Tasks: desktopicon
Name: "{group}\Start DocumentForge Service"; Filename: "{app}\service\dfdb.exe"; \
    Parameters: "serve --port {code:GetPort} --data-dir ""{#DataDir}"""; Components: service
Name: "{group}\Uninstall DocumentForge Studio"; Filename: "{uninstallexe}"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Components: studio; Flags: unchecked
Name: "startupservice"; Description: "Run the DocumentForge service automatically at Windows startup (background, as SYSTEM — needs admin approval)"; GroupDescription: "Service:"; Components: service; Flags: unchecked

[Registry]
; Per-user .dfdb file association -> opens Studio (only if the client is installed).
Root: HKCU; Subkey: "Software\Classes\.dfdb"; ValueType: string; ValueName: ""; ValueData: "DocumentForge.Database"; Components: studio; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\DocumentForge.Database"; ValueType: string; ValueName: ""; ValueData: "DocumentForge Database"; Components: studio; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\DocumentForge.Database\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#ExeName},0"; Components: studio
Root: HKCU; Subkey: "Software\Classes\DocumentForge.Database\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#ExeName}"" ""%1"""; Components: studio

[Run]
Filename: "{app}\{#ExeName}"; Description: "Launch DocumentForge Studio"; Components: studio; Flags: nowait postinstall skipifsilent
Filename: "{app}\service\dfdb.exe"; Parameters: "serve --port {code:GetPort} --data-dir ""{#DataDir}"""; \
    Description: "Start the DocumentForge service now"; Components: service; Flags: nowait postinstall skipifsilent unchecked

[Code]
var
  PortPage: TInputQueryWizardPage;

procedure InitializeWizard;
begin
  PortPage := CreateInputQueryPage(wpSelectComponents,
    'DocumentForge port',
    'Which TCP port should the DocumentForge service use?',
    'The service and Studio''s default connection use this port. ' +
    '4300 is the DocumentForge standard; change it only if 4300 is already in use.');
  PortPage.Add('Port:', False);
  PortPage.Values[0] := '{#DefaultPort}';
end;

function GetPort(Param: String): String;
begin
  Result := Trim(PortPage.Values[0]);
  if Result = '' then
    Result := '{#DefaultPort}';
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
  Ps1, Content: String;
  ResultCode: Integer;
begin
  if CurStep <> ssPostInstall then
    Exit;

  // Record the chosen port next to the app so Studio's first-run connection
  // and the service shortcut agree.
  SaveStringToFile(ExpandConstant('{app}\port.txt'), GetPort(''), False);

  // Optional: register a startup Scheduled Task (runs the service at boot as
  // SYSTEM). Elevated via a UAC prompt because the installer itself is per-user.
  if WizardIsComponentSelected('service') and WizardIsTaskSelected('startupservice') then
  begin
    Ps1 := ExpandConstant('{app}\install-service-task.ps1');
    Content :=
      '$exe = ''' + ExpandConstant('{app}\service\dfdb.exe') + '''' + #13#10 +
      '$arg = ''serve --port ' + GetPort('') + ' --data-dir "{#DataDir}"''' + #13#10 +
      '$a = New-ScheduledTaskAction -Execute $exe -Argument $arg' + #13#10 +
      '$t = New-ScheduledTaskTrigger -AtStartup' + #13#10 +
      '$p = New-ScheduledTaskPrincipal -UserId ''SYSTEM'' -RunLevel Highest' + #13#10 +
      '$s = New-ScheduledTaskSettingsSet -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1)' + #13#10 +
      'Register-ScheduledTask -TaskName ''DocumentForge Service'' -Action $a -Trigger $t -Principal $p -Settings $s -Force' + #13#10;
    SaveStringToFile(Ps1, Content, False);
    if not ShellExec('runas', 'powershell.exe',
        '-NoProfile -ExecutionPolicy Bypass -File "' + Ps1 + '"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode)
       or (ResultCode <> 0) then
      MsgBox('The startup service task could not be registered (you may have declined the admin prompt). ' +
             'You can still start the service from the Start Menu shortcut, or re-run setup.', mbInformation, MB_OK);
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
begin
  // Only prompt for elevation if we actually created the startup task.
  if (CurUninstallStep = usUninstall) and FileExists(ExpandConstant('{app}\install-service-task.ps1')) then
    ShellExec('runas', 'schtasks.exe', '/delete /tn "DocumentForge Service" /f', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;
