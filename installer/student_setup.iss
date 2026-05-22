; -- student_setup.iss --
; Inno Setup script for NTY Classroom Control Student
; Installs 3 components: Service + Agent + Watchdog

#define MyAppName "NTY Classroom Control - Student"
#define MyAppVersion "1.1"
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
; Phase 10.11 — XML for the scheduled task is no longer copied (Phase 10.9's
; schtasks /Create approach was disabled).  Kept here as a comment + the
; ReplaceXmlTokens [Code] procedure left intact so a future Phase 10.12 can
; re-enable both with a single uncomment + root-cause the original failure.
;Source: "ClassroomCtrlService.xml";       DestDir: "{app}";  Flags: ignoreversion; AfterInstall: ReplaceXmlTokens

[Run]
; Section E — clean up Phase 10.0/10.2 Windows Service if present.  Both
; commands fail silently (non-zero exit) when the service doesn't exist,
; which is the common case for fresh installs; that's fine.
Filename: "{sys}\sc.exe"; Parameters: "stop {#MyServiceName}"; Flags: runhidden; StatusMsg: "Cleaning up legacy Windows Service..."
Filename: "{sys}\sc.exe"; Parameters: "delete {#MyServiceName}"; Flags: runhidden

; Phase 10.11 — disabled scheduled task approach.  schtasks /Create was failing
; silently on tester machines (Get-ScheduledTask -TaskPath '\NTY\*' returned
; empty after install), and Inno Setup's [Run] phase doesn't surface non-zero
; exits as install errors — so the whole install reported success while the
; Service had no autostart hook at all.  Replaced with HKLM Run + immediate
; launch further down, the same pattern that already works for the Agent.
; Kept (commented) for rollback / debug history; do not uncomment without
; first root-causing the schtasks failure (likely XML token replacement or
; UTF-8/UTF-16 BOM mismatch — see Phase 10.12 future work).
;Filename: "{sys}\schtasks.exe"; Parameters: "/Create /TN ""{#MyTaskName}"" /XML ""{app}\ClassroomCtrlService.xml"" /F"; Flags: runhidden; StatusMsg: "Registering scheduled task..."

; HKLM Run for Agent (unchanged from Phase 10.2).
Filename: "{sys}\reg.exe"; Parameters: "add ""HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run"" /v ""ClassroomCtrlAgent"" /t REG_SZ /d ""\""{app}\ClassroomCtrl.Student.Agent.exe\"""" /f"; Flags: runhidden

; Phase 10.11 — HKLM Run for Service.exe (replaces the failed scheduled task).
; Auto-starts at every user logon, same mechanism as the Agent.  Service runs
; in the user's interactive session, which is the path that was confirmed
; working end-to-end by manual launch.
Filename: "{sys}\reg.exe"; Parameters: "add ""HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run"" /v ""ClassroomCtrlService"" /t REG_SZ /d ""\""{app}\ClassroomCtrl.Student.Service.exe\"""" /f"; Flags: runhidden

; Phase 10.13 — HKLM Run for Watchdog (mirrors Service + Agent pattern).
; Watchdog now actively respawns Service on crash (Phase 10.13 rewrite of
; Watchdog\Program.cs from a dormant stub into a real monitor).  Without an
; autostart hook it never gets a chance to do its job.
Filename: "{sys}\reg.exe"; Parameters: "add ""HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run"" /v ""ClassroomCtrlWatchdog"" /t REG_SZ /d ""\""{app}\ClassroomCtrl.Student.Watchdog.exe\"""" /f"; Flags: runhidden

; Phase 10.13 — auto-enable Microphone access for desktop apps (system-wide).
; Default in Windows 10+ blocks desktop apps from mic; without this, the
; Service can't capture student mic for the Mic Monitor feature, and the
; admin has to flip Settings > Privacy > Microphone on every PC by hand.
; Three keys cover the legacy HKLM machine policy, the HKCU per-user copy
; that Settings UI writes, and the NonPackaged subkey that gates desktop
; (non-UWP) apps specifically.
; WARNING: if the customer is under Group Policy that enforces mic block,
; this registry is overwritten every reboot — IT must coordinate the GPO.
; Deliberately NOT reverted in [UninstallRun]: these are system-level
; settings the user may have configured for other apps.
Filename: "{sys}\reg.exe"; Parameters: "add ""HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone"" /v ""Value"" /t REG_SZ /d ""Allow"" /f"; Flags: runhidden
Filename: "{sys}\reg.exe"; Parameters: "add ""HKCU\Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone"" /v ""Value"" /t REG_SZ /d ""Allow"" /f"; Flags: runhidden
Filename: "{sys}\reg.exe"; Parameters: "add ""HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone\NonPackaged"" /v ""Value"" /t REG_SZ /d ""Allow"" /f"; Flags: runhidden

; Outbound firewall rule for Agent (unchanged from Phase 10.0).
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""ClassroomCtrl Student Agent"" dir=out action=allow program=""{app}\ClassroomCtrl.Student.Agent.exe"" profile=any"; Flags: runhidden

; Phase 10.11 — inbound UDP 7778 for the discovery beacon.  Covers what Phase
; 10.10 Fix 4's runtime StudentFirewallService.EnsureRules also adds — the two
; coexist (idempotent + different rule names) so the install path is robust
; even if the runtime helper hasn't run yet on first launch.
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""ClassroomCtrl Student UDP"" dir=in action=allow protocol=UDP localport=7778 profile=any"; Flags: runhidden

; Phase 10.11 — outbound rule scoped to Service.exe (TCP control channel to Teacher).
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""ClassroomCtrl Student Service"" dir=out action=allow program=""{app}\ClassroomCtrl.Student.Service.exe"" profile=any"; Flags: runhidden

; Phase 10.11 — start Service.exe immediately so the tester doesn't need to
; logoff/login post-install.  nowait so the wizard doesn't block waiting for
; the long-running Service process; runhidden so no stray console flashes.
Filename: "{app}\ClassroomCtrl.Student.Service.exe"; Flags: nowait runhidden

; Phase 10.13 — start Watchdog immediately too so the respawn guarantee kicks
; in without requiring a reboot.  Same nowait + runhidden pattern as Service.
Filename: "{app}\ClassroomCtrl.Student.Watchdog.exe"; Flags: nowait runhidden

; Phase 10.11 — disabled (paired with the /Create above).  Service is now
; launched directly via the postinstall step a few lines below.
;Filename: "{sys}\schtasks.exe"; Parameters: "/Run /TN ""{#MyTaskName}"""; Flags: runhidden; StatusMsg: "Starting Classroom Service..."

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

; Phase 10.11 — clean up the Service HKLM Run entry + the two new firewall
; rules added by Phase 10.11.  Each command gets a unique RunOnceId so it
; runs at most once even if the uninstaller is invoked multiple times.
Filename: "{sys}\reg.exe"; Parameters: "delete ""HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run"" /v ""ClassroomCtrlService"" /f"; Flags: runhidden; RunOnceId: "DelServiceRun"
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""ClassroomCtrl Student UDP"""; Flags: runhidden; RunOnceId: "DelFwUdpInbound"
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""ClassroomCtrl Student Service"""; Flags: runhidden; RunOnceId: "DelFwSvcOutbound"

; Phase 10.13 — remove Watchdog HKLM Run entry on uninstall.
Filename: "{sys}\reg.exe"; Parameters: "delete ""HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run"" /v ""ClassroomCtrlWatchdog"" /f"; Flags: runhidden; RunOnceId: "DelWatchdogRun"

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
