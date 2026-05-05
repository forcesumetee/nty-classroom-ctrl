# Classroom Control System

Enterprise classroom management software for Windows (LAN / direct-IP deployment).

**Copyright © NTY MULTIMEDIA CO.,LTD**

## Project Layout
- `src/ClassroomCtrl.Shared` — Protocol DTOs, common models, message types
- `src/ClassroomCtrl.Networking` — TCP transport (manual IP)
- `src/ClassroomCtrl.Licensing` — Machine-code generator + License validator (matches NTY Cloud Keygen) + DPAPI store
- `src/ClassroomCtrl.Exam.Shared` — Exam DTOs, auto-grading
- `src/ClassroomCtrl.Teacher` — Teacher console (WPF, master)
  - `Activation/` — first-run license activation window
  - `Exam/` — Word template parser + Excel exporter (ClosedXML)
- `src/ClassroomCtrl.Student.Service` — Windows Service (LocalSystem)
- `src/ClassroomCtrl.Student.Agent` — User-session tray UI (WPF)
  - `Exam/` — Exam window + Kiosk guard (Strict tier)
- `src/ClassroomCtrl.Student.Watchdog` — Heartbeat process

## Build
```
dotnet restore
dotnet build -c Release
```

## Publish (single-file, self-contained, signed)
```
dotnet publish -c Release -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true --self-contained true
```

## Recommended post-build (security)
1. Sign all .exe and .dll with EV Code Signing Certificate
2. Run binaries through ConfuserEx (or Eazfuscator) to obfuscate license salt
3. Build .msi via WiX, sign the .msi
4. Submit signed binary to AV vendors for whitelisting

## License Activation Flow
1. First run → application checks `HKLM\Software\NTY\ClassroomCtrl\License`
2. If missing → ActivationWindow opens, displays Machine Code
3. Customer/installer sends Machine Code to NTY support
4. Support uses Cloud Keygen (FastAPI) → returns License Key
5. Customer enters Key → local validation → DPAPI-encrypted storage
6. Subsequent runs: silent validation

## Install Student endpoint (preferred path: MSI)

```
.\installers\build-student-msi.ps1
```

The script publishes all binaries (calls `publish.ps1`), stages `Service.exe`
out of the wildcard harvest path, and builds the WiX MSI. Output:

- `installers\Student.Installer\bin\Debug\NTY-ClassroomCtrl-Student-1.0.0.msi`

What the MSI installs:

- **Files** to `C:\Program Files\NTY\ClassroomCtrl\Student\` (~190 MB on disk)
- **Windows Service `ClassroomService`** running as `LocalSystem`, auto-start,
  restart-on-failure recovery (3 attempts, 5 s delay)
- **HKLM Run** registry value so the user-session **Student.Agent** auto-starts
  on every user logon
- **Start Menu shortcut** for manually launching the Agent

To deploy: copy the .msi to the student PC and double-click (admin elevation
required for the per-machine service install).

## Install Student endpoint (fallback: manual sc commands)
If you can't use the MSI for any reason, the legacy manual path still works:
```
sc create ClassroomService binPath= "C:\Program Files\NTY\ClassroomCtrl\ClassroomCtrl.Student.Service.exe" start= auto obj= LocalSystem
sc failure ClassroomService reset= 86400 actions= restart/5000/restart/5000/restart/5000
sc description ClassroomService "NTY Classroom Control endpoint service"
sc start ClassroomService
```

## See Technical_Specification.docx for full design
- §5: Manual IP connection (no auto-discovery)
- §13: Exam System (Word import / MCQ builder / Essay)
- §14: License Activation (algorithm + security limitations)

## DPI Awareness

Both Teacher and Student.Agent are declared **PerMonitorV2 DPI-aware** (via app.manifest +
runtime `SetProcessDpiAwareness(2)` fallback). This is required so screen capture sees
**physical pixels** at any Windows display scaling.

- Without DPI awareness, Windows lies to the process at 125% / 150% scaling and reports
  e.g. 1536×864 logical pixels instead of the real 1920×1080 panel. Capture would then
  produce a downscaled image and the student fullscreen view would be letterboxed.
- With PerMonitorV2 active, capture reads the panel's actual resolution and
  the broadcast hits the student edge-to-edge.

Side effect: at 125% / 150% scaling the **Teacher console UI** will look smaller than
other DPI-unaware apps. This is the expected trade-off — capture quality wins.

## Recording (Phase 5a)

Teachers can record their broadcast (screen + audio mix) to MP4 from the Teacher console.

- **Trigger:** sidebar buttons "Start Recording" / "Stop Recording"
- **Codec requirement:** H.264 mode only (MJPEG records are not supported in 5a — re-encode would burn CPU during a live class)
- **Output path:** `%ProgramData%\NTY\ClassroomCtrl\Recordings\classroom-YYYY-MM-DD-HHMMSS.mp4`
- **Format:** MP4 (H.264 video copied from broadcast NAL stream + AAC audio transcoded from 16 kHz mono PCM at 128 kbps)
- **File size estimate:** ~280 MB / hour (500 kbps video + 128 kbps audio)
- **Bundled FFmpeg:** the recorder uses an LGPL build of FFmpeg shipped inside the installer (~50 MB). See [LICENSE-FFmpeg.txt](LICENSE-FFmpeg.txt) for license terms and patent notices.
- **Crash recovery:** if Teacher.exe is killed mid-recording, raw `<id>.h264` and `<id>.wav` temp files are left in the recordings folder for manual mux with FFmpeg.

## Known Issues

### BUG-001: View Student in H.264 mode falls back to MJPEG
- **Severity:** Low (workaround in place)
- **Affected:** "View Student Fullscreen" feature (right-click student tile → "ดูหน้าจอเต็ม")
- **Behavior:** Always uses MJPEG codec for Student → Teacher direction regardless of Teacher's codec selection
- **Why:** H.264 path hangs at "Waiting for student to start streaming..." — root cause under investigation
- **Workaround:** Automatic — no user action needed
- **Status:** Tracked, planned for post-Phase-5 debugging
