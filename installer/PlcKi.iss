; PLC-Ki installer (Inno Setup 6). Build it with installer\build.ps1, which publishes the app first.
; Installs PLC-Ki to Program Files and runs it as the Windows service "PLC-Ki" under the LocalService account.
; Settings, users and uploaded models live in C:\ProgramData\PLC-Ki and are kept on update and uninstall.

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\out\publish"
#endif

#define AppName "PLC-Ki"
#define ServiceName "PLC-Ki"
#define ExeName "PlcKi.Server.exe"

[Setup]
AppId={{6C1F3E2B-5A7D-4F0E-9B8C-2D4E6A1B3C5F}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=PLC-Ki
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\out
OutputBaseFilename=PLC-Ki-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#ExeName}
CloseApplications=no
SetupLogging=yes

[Tasks]
Name: "firewall"; Description: "Allow other computers on the plant network through the Windows firewall (you still switch network access on in PLC-Ki's settings)"; Flags: unchecked
Name: "desktopicon"; Description: "Desktop shortcut to the PLC-Ki page"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Dirs]
Name: "{commonappdata}\PLC-Ki"; Flags: uninsneveruninstall

[Icons]
Name: "{group}\PLC-Ki"; Filename: "http://localhost:5080/"
Name: "{group}\PLC-Ki data folder"; Filename: "{commonappdata}\PLC-Ki"
Name: "{group}\Uninstall PLC-Ki"; Filename: "{uninstallexe}"
Name: "{autodesktop}\PLC-Ki"; Filename: "http://localhost:5080/"; Tasks: desktopicon

[Run]
; Data folder: only Administrators, SYSTEM and the service account (LocalService, S-1-5-19). It holds the user list and keys.
Filename: "{sys}\icacls.exe"; Parameters: """{commonappdata}\PLC-Ki"" /inheritance:r /grant:r *S-1-5-32-544:(OI)(CI)F *S-1-5-18:(OI)(CI)F *S-1-5-19:(OI)(CI)M"; Flags: runhidden; StatusMsg: "Protecting the data folder..."
Filename: "{sys}\sc.exe"; Parameters: "create {#ServiceName} binPath= ""\""{app}\{#ExeName}\"""" start= delayed-auto obj= ""NT AUTHORITY\LocalService"" DisplayName= ""PLC-Ki"""; Flags: runhidden; StatusMsg: "Registering the PLC-Ki service..."; Check: not ServiceExists
Filename: "{sys}\sc.exe"; Parameters: "description {#ServiceName} ""Reads the PLC (read-only) and explains in plain English what the machine is doing. Page: http://localhost:5080"""; Flags: runhidden
; Restart after a crash, and after "Restart PLC-Ki" on the settings page (it leaves with an error code)
Filename: "{sys}\sc.exe"; Parameters: "failure {#ServiceName} reset= 86400 actions= restart/2000/restart/5000/restart/30000"; Flags: runhidden
Filename: "{sys}\sc.exe"; Parameters: "failureflag {#ServiceName} 1"; Flags: runhidden
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""PLC-Ki"" dir=in action=allow program=""{app}\{#ExeName}"" profile=domain,private enable=yes"; Flags: runhidden; Tasks: firewall
Filename: "{sys}\sc.exe"; Parameters: "start {#ServiceName}"; Flags: runhidden; StatusMsg: "Starting PLC-Ki..."
Filename: "http://localhost:5080/"; Description: "Open PLC-Ki now (first time: create the administrator login)"; Flags: postinstall shellexec nowait skipifsilent

[UninstallRun]
Filename: "{sys}\sc.exe"; Parameters: "stop {#ServiceName}"; Flags: runhidden; RunOnceId: "StopService"
Filename: "{sys}\timeout.exe"; Parameters: "/t 3 /nobreak"; Flags: runhidden; RunOnceId: "WaitStop"
Filename: "{sys}\sc.exe"; Parameters: "delete {#ServiceName}"; Flags: runhidden; RunOnceId: "DeleteService"
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""PLC-Ki"""; Flags: runhidden; RunOnceId: "DeleteFirewallRule"

[Messages]
FinishedLabel=PLC-Ki is installed and running as a Windows service.%n%nOpen http://localhost:5080 on this PC to create the administrator login, then enter the PLC address on the Settings page.%n%nSettings and users are kept in C:\ProgramData\PLC-Ki.

[Code]
function ServiceExists(): Boolean;
var
  ResultCode: Integer;
begin
  Result := Exec(ExpandConstant('{sys}\sc.exe'), 'query {#ServiceName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
end;

// On an update, stop the running service before the files are replaced
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode, i: Integer;
begin
  Result := '';
  if ServiceExists() then
  begin
    Exec(ExpandConstant('{sys}\sc.exe'), 'stop {#ServiceName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    for i := 1 to 30 do
    begin
      Exec(ExpandConstant('{sys}\cmd.exe'), '/c sc query {#ServiceName} | find "STOPPED"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
      if ResultCode = 0 then Break;
      Sleep(500);
    end;
  end;
end;
