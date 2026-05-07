; -- student_setup.iss --
; Inno Setup script for NTY Classroom Control Student
; Installs 3 components: Service + Agent + Watchdog

#define MyAppName "NTY Classroom Control - Student"
#define MyAppVersion "1.0"
#define MyAppPublisher "NTY MULTIMEDIA CO.,LTD"
#define MyServiceName "NTYClassroomService"

[Setup]
AppId={{D85F1E2A-7B3C-4A1E-9F2D-NTYCLASSROOM02}}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\NTY\ClassroomCtrl\Student
DefaultGroupName=NTY Classroom Control Student
DisableProgramGroupPage=yes
PrivilegesRequired=admin
OutputDir=output
OutputBaseFilename=NTY_ClassroomCtrl_Student_Setup_v{#MyAppVersion}
Compression=lzma
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
SetupIconFile=..\assets\logo.ico
UninstallDisplayIcon={app}\ClassroomCtrl.Student.Agent.exe

[Languages]
Name: "thai"; MessagesFile: "compiler:Languages\Thai.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

; Phase 10.9 — register Service via Task Scheduler instead of Windows Service.
; The Service runs in the user's interactive session (Session 1+), where every
; feature was confirmed working in console-mode testing.  See ClassroomCtrlService.xml
; for the task definition — __APP_PATH__ is rewritten to the install path by
; the [Code] section before schtasks /Create runs.
#define MyTaskName "NTY\ClassroomCtrlService"

[Files]
Source: "..\publish\Student.Service\*";  DestDir: "{app}";  Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\publish\Student.Agent\*";    DestDir: "{app}";  Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\publish\Student.Watchdog\*"; DestDir: "{app}";  Flags: ignoreversion recursesubdirs createallsubdirs
Source: "ClassroomCtrlService.xml";       DestDir: "{app}";  Flags: ignoreversion; AfterInstall: ReplaceXmlTokens

[Run]
; Section E — clean up Phase 10.0/10.2 Windows Service if present.  Both
; commands fail silently (non-zero exit) when the service doesn't exist,
; which is the common case for fresh installs; that's fine.
Filename: "{sys}\sc.exe"; Parameters: "stop {#MyServiceName}"; Flags: runhidden; StatusMsg: "Cleaning up legacy Windows Service..."
Filename: "{sys}\sc.exe"; Parameters: "delete {#MyServiceName}"; Flags: runhidden

; Section A — register Task Scheduler entry (XML defines logon trigger + Users group).
; Idempotent via /F (force overwrite if task already exists from a previous install).
Filename: "{sys}\schtasks.exe"; Parameters: "/Create /TN ""{#MyTaskName}"" /XML ""{app}\ClassroomCtrlService.xml"" /F"; Flags: runhidden; StatusMsg: "Registering scheduled task..."

; HKLM Run for Agent (unchanged from Phase 10.2).
Filename: "{sys}\reg.exe"; Parameters: "add ""HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run"" /v ""ClassroomCtrlAgent"" /t REG_SZ /d ""\""{app}\ClassroomCtrl.Student.Agent.exe\"""" /f"; Flags: runhidden

; Outbound firewall rule for Agent (unchanged from Phase 10.0).
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""ClassroomCtrl Student Agent"" dir=out action=allow program=""{app}\ClassroomCtrl.Student.Agent.exe"" profile=any"; Flags: runhidden

; First-time start so the customer doesn't have to logout/login post-install.
; The LogonTrigger fires automatically on every subsequent login.
Filename: "{sys}\schtasks.exe"; Parameters: "/Run /TN ""{#MyTaskName}"""; Flags: runhidden; StatusMsg: "Starting Classroom Service..."

; Launch Agent so the postinstall UI prompt has somewhere to appear.
Filename: "{app}\ClassroomCtrl.Student.Agent.exe"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Section A — tear down the scheduled task.  RunOnceId guarantees each command
; runs only once even if the user uninstalls multiple times in sequence.
Filename: "{sys}\schtasks.exe"; Parameters: "/End /TN ""{#MyTaskName}"""; Flags: runhidden; RunOnceId: "EndTask"
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /TN ""{#MyTaskName}"" /F"; Flags: runhidden; RunOnceId: "DeleteTask"

; Belt-and-suspenders: also tear down any legacy Windows Service that survived from
; pre-10.9 installs and never got cleaned up by an in-place upgrade.
Filename: "{sys}\sc.exe"; Parameters: "stop {#MyServiceName}"; Flags: runhidden; RunOnceId: "LegacySvcStop"
Filename: "{sys}\sc.exe"; Parameters: "delete {#MyServiceName}"; Flags: runhidden; RunOnceId: "LegacySvcDelete"

Filename: "{sys}\reg.exe"; Parameters: "delete ""HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run"" /v ""ClassroomCtrlAgent"" /f"; Flags: runhidden; RunOnceId: "DelAgentRun"
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM ClassroomCtrl.Student.Service.exe"; Flags: runhidden; RunOnceId: "KillService"
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM ClassroomCtrl.Student.Agent.exe"; Flags: runhidden; RunOnceId: "KillAgent"
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM ClassroomCtrl.Student.Watchdog.exe"; Flags: runhidden; RunOnceId: "KillWatchdog"
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""ClassroomCtrl Student Agent"""; Flags: runhidden; RunOnceId: "DelFirewall"

; Phase 10.1 Section B — preserve customer data on upgrade.  Previously
; wiped {commonappdata}\NTY\ClassroomCtrl which contains config.txt (the
; configured Teacher IP from Phase 8 Section B); a routine reinstall
; therefore unconfigured every student PC.  Removed.  True uninstall
; leaves the folder behind for manual IT cleanup.
[UninstallDelete]
Type: filesandordirs; Name: "{app}"

[Code]
// Phase 10.9 - rewrite the __APP_PATH__ placeholders inside the Task Scheduler
// XML to the actual install path before schtasks.exe /Create reads the file.
// Bound to AfterInstall on the .xml [Files] entry so the rewrite happens
// immediately after extraction, before any [Run] entry executes.
//
// The XML is stored as UTF-8 (not UTF-16, despite the schtasks.exe historical
// preference for UTF-16) so this AnsiString-based StringChangeEx can match the
// literal ASCII placeholder. schtasks.exe on Windows 10/11 accepts UTF-8 XML.
procedure ReplaceXmlTokens;
var
  XmlPath: string;
  XmlBytes: AnsiString;
  XmlContent: string;
  AppPath: string;
begin
  XmlPath := ExpandConstant('{app}\ClassroomCtrlService.xml');
  AppPath := ExpandConstant('{app}');
  // LoadStringFromFile / SaveStringToFile use AnsiString (raw bytes).
  // StringChangeEx uses Unicode String. The XML is pure ASCII, so the
  // system-codepage round-trip via String() / AnsiString() casts is byte-lossless.
  if LoadStringFromFile(XmlPath, XmlBytes) then
  begin
    XmlContent := String(XmlBytes);
    StringChangeEx(XmlContent, '__APP_PATH__', AppPath, True);
    XmlBytes := AnsiString(XmlContent);
    SaveStringToFile(XmlPath, XmlBytes, False);
  end;
end;
