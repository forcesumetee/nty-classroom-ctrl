; -- teacher_setup.iss --
; Inno Setup script for NTY Classroom Control Teacher
; Compile with: ISCC.exe teacher_setup.iss

#define MyAppName "NTY Classroom Control - Teacher"
#define MyAppVersion "1.1"
#define MyAppPublisher "NTY MULTIMEDIA CO.,LTD"
#define MyAppExeName "ClassroomCtrl.Teacher.exe"

[Setup]
AppId={{D85F1E2A-7B3C-4A1E-9F2D-NTYCLASSROOM01}}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\NTY\ClassroomCtrl\Teacher
DefaultGroupName=NTY Classroom Control
DisableProgramGroupPage=yes
PrivilegesRequired=admin
OutputDir=output
OutputBaseFilename=NTY_ClassroomCtrl_Teacher_Setup_v{#MyAppVersion}
Compression=lzma
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
SetupIconFile=..\assets\logo.ico
UninstallDisplayIcon={app}\{#MyAppExeName}

[Languages]
Name: "thai"; MessagesFile: "compiler:Languages\Thai.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "..\publish\Teacher\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
; Phase 10.1 Section A — rule names match Phase 9.1 FirewallService.cs
; (TcpRuleName / UdpRuleName) so installer + runtime EnsureRules() agree.
; First launch after install: Phase 9.1 RuleExists() finds both → no UAC prompt.
Filename: "netsh"; Parameters: "advfirewall firewall add rule name=""ClassroomCtrl Teacher TCP (auto)"" dir=in action=allow protocol=TCP localport=7777 profile=any"; Flags: runhidden; StatusMsg: "Adding firewall rules..."
Filename: "netsh"; Parameters: "advfirewall firewall add rule name=""ClassroomCtrl Teacher UDP (auto)"" dir=in action=allow protocol=UDP localport=7778 profile=any"; Flags: runhidden
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "netsh"; Parameters: "advfirewall firewall delete rule name=""ClassroomCtrl Teacher TCP (auto)"""; Flags: runhidden
Filename: "netsh"; Parameters: "advfirewall firewall delete rule name=""ClassroomCtrl Teacher UDP (auto)"""; Flags: runhidden
Filename: "taskkill.exe"; Parameters: "/F /IM {#MyAppExeName}"; Flags: runhidden

; Phase 10.1 Section B — preserve customer data on upgrade.  The previous
; entry that wiped {commonappdata}\NTY\ClassroomCtrl deleted roster JSONs,
; branding.json, and config.txt every reinstall.  Removed.  True uninstall
; leaves a small orphan folder that IT can clean manually if needed; the
; data-loss risk on routine upgrades is the bigger concern.
[UninstallDelete]
Type: filesandordirs; Name: "{app}"
