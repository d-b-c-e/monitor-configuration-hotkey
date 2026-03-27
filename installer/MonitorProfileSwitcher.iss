; MonitorProfileSwitcher Inno Setup Script
; Requires Inno Setup 6.x

#define MyAppName "Monitor Profile Switcher"
#define MyAppExeName "MonitorProfileSwitcher.exe"
#define MyAppPublisher "MonitorProfileSwitcher"
#define MyAppURL "https://github.com/d-b-c-e/monitor-configuration-hotkey"

; Version is passed in via command line: /DMyAppVersion=1.0.0
#ifndef MyAppVersion
  #define MyAppVersion "0.0.0"
#endif

[Setup]
AppId={{B7E3A1F9-5C2D-4E8B-9F1A-3D6C8E2B4A7F}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
AppUpdatesURL={#MyAppURL}/releases
DefaultDirName={autopf}\MonitorProfileSwitcher
DefaultGroupName={#MyAppName}
AllowNoIcons=yes
OutputDir=..\dist
OutputBaseFilename=MonitorProfileSwitcher-{#MyAppVersion}-Setup
SetupIconFile=..\MonitorProfileSwitcher\app.ico
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
; Close running app before upgrade
CloseApplications=force
CloseApplicationsFilter=*.exe
RestartApplications=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "startwithwindows"; Description: "Start with Windows (recommended)"; GroupDescription: "System Integration:"

[Files]
; Main application files from publish directory
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"

[Run]
; Create scheduled task for "Start with Windows" (reliable with Fast Startup unlike Run key)
Filename: "schtasks.exe"; Parameters: "/Create /TN ""MonitorProfileSwitcher"" /TR """"""{app}\{#MyAppExeName}"""""" /SC ONLOGON /RL LIMITED /DELAY 0000:05 /F"; Tasks: startwithwindows; Flags: runhidden
; Launch after install
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent shellexec runasoriginaluser

[Registry]
; Clean up legacy Run key entries (in case user previously added one manually)
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "MonitorProfileSwitcher"; Flags: deletevalue

[Code]
// Remove scheduled task on uninstall
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    Exec('schtasks.exe', '/Delete /TN "MonitorProfileSwitcher" /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;
end;
