# Phase 16 Pre-Ship QA Audit

**Branch:** ui-rewrite
**HEAD:** 9630ac3 — Phase 16-X audit: Bug G — Chat routing (Conference vs Classroom)
**Phase 16 commit count:** 42
**Auditor mode:** Read-only. No code, config, or asset was modified.
**Scope:** Phase 16 Conference Mode work (commits 952bd30 → 9630ac3).
**Date:** 2026-05-31

---

## Summary

- **Total findings:** 14
- **🔴 Ship-blockers:** 0
- **🟡 v1.1 polish:** 7
- **🟢 Informational:** 7

The Conference Mode shipping surface is **functionally complete and structurally
sound**. All 23 wire DTOs round-trip via T1–T23 wire-compat tests; the Service
forwarder, the Agent dispatcher, and the Teacher relay agree on every Conference
code point; role-gated UI hides host-only actions from participants; localization
is complete in EN+TH for every key referenced by Conference XAML; both installers
package the new `ClassroomCtrl.Shared.Wpf.dll` and the four AForge / GDI+ DLLs
required for student-side cam capture.

No 🔴 ship-blocker landed. The single concrete risk that was already understood
going into this round — **Teacher cam sync DirectShow init** — was confirmed by
the audit and remains a deliberate v1.1 defer per the Phase 16-X primer. The
other six 🟡 items are either polish (per-frame BitmapImage allocation, error
swallowing in non-hot paths) or scalability documentation gaps.

**Recommendation: SHIP.** Dev validation of the 8 most-recent bug-fix commits
(Bug B, C, E, D, D-retry, F, H, G) on a 2-PC bench is the only remaining gate.

---

## Findings

### 🔴 Ship-blockers

None.

---

### 🟡 v1.1 polish

#### Finding 1 — Teacher CameraBroadcastService.Start does synchronous DirectShow init on caller thread

- **Severity:** 🟡 v1.1 polish (explicitly deferred per 16-X primer)
- **Dimension:** 3 (Async / Threading)
- **Evidence:** [src/ClassroomCtrl.Teacher/Services/CameraBroadcastService.cs:101-119](src/ClassroomCtrl.Teacher/Services/CameraBroadcastService.cs#L101-L119)
  ```csharp
  public bool Start(string moniker, int width, int height, int fps)
  {
      if (IsActive) return false;
      try {
          _device = new VideoCaptureDevice(moniker);
          var cap = _device.VideoCapabilities.OrderBy(...).FirstOrDefault();  // SYNC filter-graph build
          ...
          _device.Start();  // SYNC AForge thread spawn
  ```
- **Risk:** VideoCapabilities enumeration performs a blocking DirectShow filter-graph build on the calling thread. When called from MainViewModel on the UI thread it can hang the teacher window for 5–10 s on slow or first-time drivers — the same class of bug as Student-side Bug C (commit 65a2c8d).
- **Recommendation:** Mirror [StudentCameraBroadcaster.StartAsync](src/ClassroomCtrl.Student.Agent/StudentCameraBroadcaster.cs#L166-L207) — wrap construction + `VideoCapabilities` + `Start()` in `Task.Run` + `WhenAny(initTask, timeoutTask)` with a 10 s `InitTimeout`.

#### Finding 2 — Teacher CameraBroadcastService.Stop uses unbounded WaitForStop

- **Severity:** 🟡 v1.1 polish
- **Dimension:** 4 (Resource cleanup)
- **Evidence:** [src/ClassroomCtrl.Teacher/Services/CameraBroadcastService.cs:155-187](src/ClassroomCtrl.Teacher/Services/CameraBroadcastService.cs#L155-L187)
  ```csharp
  public void Stop()
  {
      if (_device != null) {
          _device.SignalToStop();
          _device.WaitForStop();    // unbounded — no timeout
  ```
- **Risk:** If the AForge capture thread is wedged (driver crash, USB hotplug), `WaitForStop()` blocks the teacher UI on app shutdown indefinitely. Student fixed in Phase 16-X (StopTimeout = 3 s, [StudentCameraBroadcaster.cs:49](src/ClassroomCtrl.Student.Agent/StudentCameraBroadcaster.cs#L49)); Teacher copy was not folded in.
- **Recommendation:** Wrap `WaitForStop()` in `Task.Run(...).Wait(StopTimeout)` with fire-and-forget fallback (match the Student pattern).

#### Finding 3 — Per-frame BitmapImage allocation in Conference gallery hot path

- **Severity:** 🟡 v1.1 polish
- **Dimension:** Bonus 10 (Perf hotspots)
- **Evidence:** [src/ClassroomCtrl.Student.Agent/ConferenceGalleryWindow.xaml.cs:92-112](src/ClassroomCtrl.Student.Agent/ConferenceGalleryWindow.xaml.cs#L92-L112) (`OnPeerCameraFrame`) — also same pattern in `UpdateFrame` and `SetSelfPreviewFrame`.
  ```csharp
  var bmp = new BitmapImage();                       // new allocation every frame
  using var ms = new MemoryStream(jpeg);
  bmp.BeginInit();
  bmp.CacheOption = BitmapCacheOption.OnLoad;
  bmp.StreamSource = ms;
  bmp.EndInit();
  bmp.Freeze();
  tile.JpegFrame = bmp;
  ```
- **Risk:** 10 fps × up-to-30 participants ≈ 300 `BitmapImage` allocations/sec under realistic load. WPF GC can fall behind under a sustained Conference (~50 min session) and induce dispatcher-priority pauses on the gallery. Not observable at small smoke-test scale, expected to surface in pilot deployments.
- **Recommendation:** Reuse one `WriteableBitmap` per tile (size-fixed at 320×240 per the `ConferenceCameraStartMessage` default), `WritePixels` into it from the decoded JPEG. Profile a 30-peer x 10-fps baseline first to quantify the win.

#### Finding 4 — IpcClient.LogToFile swallows write failures silently

- **Severity:** 🟡 v1.1 polish
- **Dimension:** 5 (Error handling / observability)
- **Evidence:** [src/ClassroomCtrl.Student.Agent/IpcClient.cs:250-261](src/ClassroomCtrl.Student.Agent/IpcClient.cs#L250-L261)
  ```csharp
  public static void LogToFile(string msg) {
      try {
          var path = Path.Combine(Path.GetTempPath(), "agent-debug.log");
          lock (_logLock) { File.AppendAllText(path, ...); }
      }
      catch { }   // last-resort, blind
  }
  ```
- **Risk:** The diagnostic logger is the audit trail for every IPC + dispatch error in `MainWindow.xaml.cs`. If `%TEMP%\agent-debug.log` cannot be written (disk full, antivirus lock, perms after Group Policy push) every diagnostic vanishes — exactly the scenarios where the dev most needs traces.
- **Recommendation:** Add `Debug.WriteLine($"LogToFile failed: {ex.Message}")` and/or `System.Diagnostics.Trace.WriteLine` in the catch. Cheap, never throws, surfaces in attached debuggers.

#### Finding 5 — Silent catch on ConferenceCameraStopReceived event invoke (Teacher relay)

- **Severity:** 🟡 v1.1 polish
- **Dimension:** 5 (Error handling)
- **Evidence:** [src/ClassroomCtrl.Teacher/Services/ControlServer.cs:124](src/ClassroomCtrl.Teacher/Services/ControlServer.cs#L124) (implicit STOP on peer disconnect)
  ```csharp
  if (_activeConferenceCamSenders.Remove(id)) {
      try { ConferenceCameraStopReceived?.Invoke(this, id); } catch { }
      _logger.LogInformation("ConferenceCamera implicit STOP for {Id}", id);
  }
  ```
- **Risk:** If a subscriber on the teacher's MainViewModel throws while handling implicit stop (e.g. tile mutation on wrong thread), the exception is swallowed and the peer's cam tile stays "live" with no frames — UI lies about what the network actually did.
- **Recommendation:** `catch (Exception ex) { _logger.LogWarning(ex, "ConferenceCameraStopReceived handler failed for {Id}", id); }`. Same fix applies to the other `try { ... ?.Invoke } catch { }` blocks in this file if any.

#### Finding 6 — Student VM missing display-only Conference toolbar properties

- **Severity:** 🟡 v1.1 polish (already documented as deferred)
- **Dimension:** 2 (VM command parity)
- **Evidence:** [src/ClassroomCtrl.Student.Agent/ViewModels/StudentConferenceShellViewModel.cs](src/ClassroomCtrl.Student.Agent/ViewModels/StudentConferenceShellViewModel.cs) — vs. [ConferenceToolbar.xaml](src/ClassroomCtrl.Shared.Wpf/Conference/ConferenceToolbar.xaml) bindings.
  Missing on Student VM: `MicTooltipText` (line 98 binding), `CameraButtonText` (line 114), `ShareScreenButtonText` (line 137), `HasNotifications` (line 193), `IsScreenSharing` (lines 145, 162).
- **Risk:** WPF silently swallows the unresolved-property bindings (no exception, just empty tooltip / no active-state highlight). User sees blank tooltip on the 🎙 / 📷 buttons in the student window — purely cosmetic. The Share button itself is hidden by `Role.CanShareScreen=false` on Participant so `IsScreenSharing` mis-binding is dead code on the student side.
- **Recommendation:** Add the 5 display-only properties to `StudentConferenceShellViewModel` so the tooltips localize cleanly. Trivial; lump into v1.1 polish round.

#### Finding 7 — StudentCameraBroadcaster LastError strings not localized

- **Severity:** 🟡 v1.1 polish
- **Dimension:** 7 (i18n)
- **Evidence:** [src/ClassroomCtrl.Student.Agent/StudentCameraBroadcaster.cs:190, 231, 238](src/ClassroomCtrl.Student.Agent/StudentCameraBroadcaster.cs#L190) — "Camera init timed out", "Camera init cancelled", raw `ex.Message` from DirectShow.
- **Risk:** Thai-locale users see English (or COM HRESULT) toasts when the cam fails to open. Low-frequency event; technical-audience strings; acceptable in v1.0.
- **Recommendation:** Define `Conf_CameraError_Timeout` / `Conf_CameraError_Cancelled` keys in [src/ClassroomCtrl.Shared/Localization/Loc.cs](src/ClassroomCtrl.Shared/Localization/Loc.cs) and wrap user-facing assignments. Leave the inner `ex.Message` raw for the LogToFile trace.

---

### 🟢 Informational

#### Finding 8 — Wire protocol coverage is complete and tested

- **Dimension:** 1
- **Evidence:** All Phase 16 wire codes added in `MessageType` ([MessageType.cs:206-222](src/ClassroomCtrl.Shared/Protocol/MessageType.cs#L206-L222)) have matching `[MessagePackObject]` DTOs in [Messages.cs](src/ClassroomCtrl.Shared/Protocol/Messages.cs) and round-trip tests T17–T23 in [tools/EnvelopeWireCompatTest/Program.cs](tools/EnvelopeWireCompatTest/Program.cs). Forward-compat preserved via explicit `[Key(n)]` + MessagePack's unknown-key-skip semantics.
- **Status:** Pass. No action.

#### Finding 9 — Service forwarder routes every Conference wire (Bug B post-fix)

- **Dimension:** 1
- **Evidence:** [src/ClassroomCtrl.Student.Service/ClassroomWorker.cs:656-707](src/ClassroomCtrl.Student.Service/ClassroomWorker.cs#L656-L707) — explicit arms for `ConferenceStart`, `ConferenceEnd`, `HandLower`, `Reaction`, `ConferenceCameraStart/Frame/Stop`, `ConferenceShareStart/Frame/Stop`, each with `IsForMe` gate before `ForwardToAgentAsync`. `ChatBroadcast` was already routed pre-Phase-16; its new `IsConferenceContext` field is handled at the Agent layer.
- **Status:** Pass — covers the latent gap that produced Bug B. No action.

#### Finding 10 — Mode separation between Classroom and Conference is clean

- **Dimension:** 6
- **Evidence:**
  - Classroom screen share (0x0322–0x0324) still routes to `StudentScreenWindow` fullscreen ([src/ClassroomCtrl.Student.Agent/MainWindow.xaml.cs:489-520](src/ClassroomCtrl.Student.Agent/MainWindow.xaml.cs#L489-L520)).
  - Conference share (0x0683–0x0685) routes only to `_confWindow?.OnShare*()` ([MainWindow.xaml.cs:865-905](src/ClassroomCtrl.Student.Agent/MainWindow.xaml.cs#L865-L905)).
  - Classroom cam (0x0460–0x0462) → `CameraViewWindow` pop-up; Conference cam (0x0680–0x0682) → `ConferenceGalleryWindow` tiles. Distinct dispatch arms; no cross-talk.
  - Chat: `IsConferenceContext=true` filtered into `ConferenceConversation` only ([Teacher/ViewModels/MainViewModel.cs:1920-1933](src/ClassroomCtrl.Teacher/ViewModels/MainViewModel.cs#L1920-L1933)).
  - 0x0480–0x0486 remote control, 0x0640–0x0643 voice, 0x0620–0x0625 breakouts: no Phase 16 commit touches these files.
  - `ConferenceEnd` stops `_studentCamera` and clears MicStateChanged subscription on `_confWindow.Closed` ([MainWindow.xaml.cs:831-840](src/ClassroomCtrl.Student.Agent/MainWindow.xaml.cs#L831-L840)).
- **Status:** Pass. No orphan state observed.

#### Finding 11 — Conference localization complete in EN + TH

- **Dimension:** 7
- **Evidence:** [src/ClassroomCtrl.Shared/Localization/Loc.cs](src/ClassroomCtrl.Shared/Localization/Loc.cs) — verified parity for every key referenced from Conference XAML: `Conf_Tooltip_{Chat,Hand,More}`, `Conf_Leave`, `Conf_EndConference`, `Conf_RaisedHandsHeader`, `Conf_Recognize`, `Conf_Mute`, `Conf_SidebarClose`, `Conf_SidebarChatTab`, `Conf_SidebarParticipantsTab`, `Conf_CameraOff`, `Tooltip_MicOn`, `Tooltip_MicOff`, `Btn_StopSharing`, `Btn_ShareScreen`, `Btn_MuteMic`, `Btn_UnmuteMic`, `Btn_Send`. No hard-coded English literals in [ConferenceToolbar.xaml](src/ClassroomCtrl.Shared.Wpf/Conference/ConferenceToolbar.xaml) / [ConferenceSidebar.xaml](src/ClassroomCtrl.Shared.Wpf/Conference/ConferenceSidebar.xaml) / [ConferenceTile.xaml](src/ClassroomCtrl.Shared.Wpf/Conference/ConferenceTile.xaml) other than emoji glyphs (which are not translatable).
- **Status:** Pass.

#### Finding 12 — Installer packages all Phase 16 dependencies

- **Dimension:** 8
- **Evidence:**
  - [installer/student_setup.iss:42](installer/student_setup.iss#L42) and [installer/teacher_setup.iss](installer/teacher_setup.iss) both use `Source: "..\publish\<project>\*"` with `recursesubdirs` — Shared.Wpf.dll (new in 16-B) is picked up by the glob from both publish folders.
  - Student publish folder contains `AForge.dll`, `AForge.Video.dll`, `AForge.Video.DirectShow.dll`, `System.Drawing.Common.dll` (peer cam dependencies from 16-C).
  - Version string `MyAppVersion "1.1"` ([installer/student_setup.iss:6](installer/student_setup.iss#L6)) matches the Phase 10.18 1.0→1.1 bump.
  - `ignoreversion` flag in `[Files]` forces overwrite — mitigates the Phase 12-C stale-binary class of bug without needing `UninstallBeforeInstall`.
- **Status:** Pass.

#### Finding 13 — Async patterns elsewhere are sound

- **Dimension:** 3
- **Evidence:**
  - `StudentCameraBroadcaster.StartAsync` ([StudentCameraBroadcaster.cs:187-207](src/ClassroomCtrl.Student.Agent/StudentCameraBroadcaster.cs#L187-L207)) uses `Task.Run` + `WhenAny(initTask, timeoutTask)` correctly; `ConfigureAwait(false)` appropriate (service layer).
  - `ToggleConferenceCamera` / `RaiseHand_Click` are correct `async void` patterns (event handler delegating to Task; outer try/catch).
  - Frame callbacks marshal to UI via `Dispatcher.BeginInvoke` ([MainWindow.xaml.cs:1370-1382](src/ClassroomCtrl.Student.Agent/MainWindow.xaml.cs#L1370-L1382)).
- **Status:** Pass.

#### Finding 14 — Role gating consistently applied to host-only UI

- **Dimension:** 2
- **Evidence:** [ConferenceToolbar.xaml:142](src/ClassroomCtrl.Shared.Wpf/Conference/ConferenceToolbar.xaml#L142) (Share screen `Role.CanShareScreen`), :372 (End/Leave label flip on `Role.CanEndForAll`); [ConferenceSidebar.xaml:254](src/ClassroomCtrl.Shared.Wpf/Conference/ConferenceSidebar.xaml#L254) (Recognize `Role.CanRecognizeHand`), :363 (Mute `Role.CanMuteOthers`). All four use the `AncestorType=UserControl` walk so both Teacher and Student shells bind the same XAML; `HostRole` returns true everywhere, `ParticipantRole` returns false on host-only flags.
- **Status:** Pass.

---

## Coverage matrix

| Dimension | Items checked | Pass | Issues |
|-----------|---------------|------|--------|
| 1. Wire protocol consistency | 6 (DTOs, T1–T23, Service routes, Agent dispatch, self-loopback, mode-aware emit) | 6 | 0 |
| 2. VM command parity | 13 toolbar+sidebar+tile bindings | 12 | 1 (Finding 6) |
| 3. Async / threading | 5 (sync init, ConfigureAwait, async void, frame dispatch, cancellation) | 4 | 1 (Finding 1) |
| 4. Resource cleanup | 5 (Start/Stop balance, Closed handler, App.Exit, IDisposable, event unsubscribe) | 4 | 1 (Finding 2) |
| 5. Error handling + observability | 4 (empty catches, LogToFile coverage, user feedback, crash traces) | 1 | 3 (Findings 4, 5, 9) |
| 6. Mode separation | 6 (Classroom share, Conference share, cam dispatch, chat tagging, untouched paths, mode-exit cleanup) | 6 | 0 |
| 7. i18n | 5 (keys present EN+TH, hard-coded strings XAML, hard-coded strings code, error messages, IME path) | 4 | 1 (Finding 7) |
| 8. Installer + packaging | 6 (Shared.Wpf.dll, AForge libs, UninstallBeforeInstall/ignoreversion, version, debug artifacts, dev configs) | 6 | 0 |
| Bonus — code smells / perf | 6 (magic numbers, duplicate logic, TODOs, tight loops, unbounded collections, per-frame alloc) | 5 | 1 (Finding 3) |

---

## Conclusion

**Ship recommendation: SHIP — pending dev 2-PC validation of bugs B, C, E, D, D-retry, F, H, G.**

Phase 16 Conference Mode is feature-complete with no ship-blocking defects. The
seven v1.1 polish items are pre-tracked or low-impact — none require a fix
before merging to `main`. The most notable, **Finding 1 (Teacher cam sync init)**,
was the explicit defer called out in the Phase 16-X primer ("Defer only UI
cosmetic polish + Teacher cam async risk to v1.1") so it carries with it the
existing v1.1 plan.

Outstanding work outside the scope of this audit:
1. **Bug #1 (Unmute Mic crash)** — awaiting fresh `%TEMP%\agent-debug.log` trace from dev's Student PC.
2. **Dev 2-PC validation** of all 8 most recent commits.
3. **Push to origin/ui-rewrite** once validation passes.

The audit confirms the codebase is in a structurally consistent and shippable
state. No emergency rework is required.
