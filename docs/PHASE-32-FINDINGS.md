# Phase 32 Findings — System integration (Milestone 23, Mac-side) · STUDENT TRACK COMPLETE

**Goal:** turn the working macOS student subsystems (M15–M22) into a **shippable background app** —
config persistence, menubar status, permission onboarding, auto-start, and a proper `.app` bundle.
No wire change, no shipped-repo change.
**Result:** ✅ **LIVE-CONFIRMED (2026-07-13)** in **bundle form** on macOS 26.5.2 — auto-start,
onboarding, config, menubar, and the enforced lock all work against the shipped Windows Teacher.
See `docs/PHASE-32-LIVE-CONFIRMATION.md`. **Completes the Student track (scenario 2).**

**Sub-phases:** 32-A investigation · 32-B config · 32-C menubar · 32-D permissions onboarding ·
32-E LaunchAgent · 32-F bundle · 32-G LIVE (+ the Local Network Privacy fix).

## Investigation (32-A) — the process-architecture reframe
The shipped Windows student's Service/Agent/Watchdog **split looks like privilege separation but
isn't** (traced to `installer/student_setup.iss` + `Student.Watchdog/Program.cs`): all three run in
the **user session** via HKLM\Run; the LocalSystem service was abandoned (Phase 10.3 — Session-0
crash-looped WPF). The Watchdog respawns the **Service only**, never the Agent (a quit Agent stays
gone until next login). **Product decisions (locked):**
1. **Single user-session process (one LaunchAgent)** — macOS needs the window server for every job
   (capture/shield/tap/UI), which *forces* a LaunchAgent (a LaunchDaemon can't reach the GUI); and
   the current scope has **zero privileged ops** (power/USB/firewall are received-but-not-enforced),
   so no root daemon is needed. The Sandbox had already collapsed to this shape — it's *correct*,
   not a shortcut. (A privileged `SMAppService` helper is a *future* track if power/policy is built.)
2. **`KeepAlive=false` + `RunAtLoad=true`** — faithful to the Windows Agent (quit stays quit until
   next login) and it preserves the **M22 Cmd+Q dead-man escape**. `KeepAlive=true` was ruled out
   (unquittable, contradicts M22).
3. **Config = a JSON file** under `~/Library/Application Support` (unprivileged, admin-pre-seedable),
   mirroring the Windows file-model (Teacher IP moved OFF the registry for the same reason).

## 32-B config persistence
`StudentConfig` → `~/Library/Application Support/NTY ClassroomCtrl/config.json` (verified:
`SpecialFolder.ApplicationData` resolves there on .NET 10 macOS — NOT `~/.config`). Fields:
`teacherIp`, `port`, `displayName` (default = host name, like the Windows `MachineName` broadcast),
`channelId`. Load at startup, save on connect; missing/corrupt → normalized defaults, never throws;
admin-pre-seedable (documented camelCase). `--configtest` 14/14.

## 32-C menubar status
Avalonia `TrayIcon` → `NSStatusItem` (no native code). `TrayPresenter` (pure) maps
`(status, locked, ip)` → glyph/tooltip/status-line, **lock takes precedence**. Drawn icons (dot per
connection state, padlock for locked — deterministic, no emoji-font dependency). Menu: status header
+ Show Debug Window + (32-E) Start at Login + Quit. **Menubar-app lifecycle:**
`ShutdownMode.OnExplicitShutdown` + close→Hide; **Quit = clean exit = dead-man unlock**. `--traytest`
10/10.

## 32-D permissions onboarding
`Permissions` aggregates the four TCC checks reusing the **same native symbols** (Screen M16, Camera
M19, Mic M20, Accessibility M22). Tiers: **Screen Recording = Required/primary** (needs an app
**restart** after granting — TCC caches capture at launch); Camera/Mic/Accessibility = **Optional**
(effective immediately; the lock degrades gracefully without Accessibility). `PermissionsWindow`:
one row each (name/purpose/tier/live pill + Grant/Recheck). Shown at startup **only when Screen isn't
granted** (no nagging); reopenable via the tray. The app **runs with only Screen granted**.
`--permtest` 13/13.

## 32-E LaunchAgent auto-start
`LaunchAgentManager`: `~/Library/LaunchAgents/com.nty.classroomctrl.student.plist`, `RunAtLoad=true`,
`KeepAlive=false`; Enable/Disable via modern `launchctl bootstrap gui/$UID` / `bootout` (legacy
`load`/`unload` fallback). **Executable-path footgun solved:** the plist **self-targets
`Environment.ProcessPath`**, and Enable is **gated on `IsBundled`** (path under `/Contents/MacOS/`) —
so a `dotnet-run` path can never be registered; the real install only happens from the `.app`.
Disable = unload + delete = **complete removal (no trace)**. Injectable dir/programPath/launchctl →
`--launchagenttest` 16/16 installs **ZERO real agents**.

## 32-F .app bundle finalization
`scripts/package-app.sh` + `Info.plist` → the shippable-mode student: **stable
`CFBundleIdentifier com.nty.classroomctrl.student`** (TCC binds to it), **`LSUIElement=true`**
(menubar-only), version 1.2, `LSMinimumSystemVersion 12.3`. **Dylib loader path (the risk):**
`libNtyCapture.dylib` in `Contents/MacOS/` next to the apphost = the .NET `DllImport` base-directory
search path, and its deps are all absolute system paths (no `@rpath`) — so `[LibraryImport]` resolves
the *bundled* dylib with **no `install_name_tool`/rpath surgery**. **Runtime-verified:** the bundled
exe's startup `Permissions.Check(Screen)` P/Invokes the dylib with no `DllNotFound`. App is
menubar-only when bundled (window not auto-shown; Show Debug Window reveals the tabs), full window in
`dotnet run` dev. Script self-verifies (`plutil`, bundle id, dylib present + `@rpath`-free).

## 32-G LIVE + the Local Network Privacy finding (durable)
LIVE-confirmed in bundle form (see the LIVE doc). One bug surfaced and was fixed:

**macOS Local Network Privacy (macOS 15+/26) blocks a bundled app's local-subnet connect.**
- Symptom: the `.app` couldn't `connect()` to the Teacher at 172.20.10.x while `dotnet run` could —
  same code/network; `ping`/`nc` worked. Bundle-only.
- **Root cause (NOT entitlements):** the bundle is ad-hoc (`flags=0x2`), **not sandboxed**, not
  hardened — so `com.apple.security.network.client` is inert (it only applies under App Sandbox,
  which must NOT be enabled). The gate is **Local Network Privacy**: a distinct bundled app identity
  needs consent to reach the local network, and the bundle lacked **`NSLocalNetworkUsageDescription`**
  → the OS silently denied the connect. `dotnet run` runs under Terminal's granted context (the A/B).
- **Fix:** add `NSLocalNetworkUsageDescription` (an Info.plist key — no codesign/entitlement change);
  first connect prompts to allow local-network access → grant → connects (grant binds to the stable
  id). `package-app.sh` guards the key. **This recurs in the Teacher track** — a Mac Teacher
  *accepting* incoming local connections (TcpControlServer on 7777) hits the same gate. Saved as a
  memory (`macos-local-network-privacy`).

## Feasibility / scope
- **Zero wire changes** ✅ · **zero shipped-repo changes** ✅ · Sandbox + `native/` + `scripts/` +
  `tools/MockTeacher` only. Native dylib byte-unchanged (system integration is C#/scripts/plist).
- **The `.app` IS the shippable-mode student** — dev and shippable converged at 32-F.

## Verification
- Build 0 errors · **T1–T27 PASS** · `--configtest` 14/14 · `--traytest` 10/10 · `--permtest` 13/13
  · `--launchagenttest` 16/16 · prior modes (selftest/streamtest/streamtest-h264/cameratest/
  audiotest/locktest/inputtest) intact · bundle self-verify + runtime dylib resolution.

## Deferred → Phase 35 (distribution)
Developer ID **codesign + notarization** (a stable signature stops the ad-hoc-rebuild TCC re-prompt —
a *relaunch* of the same built bundle already keeps grants) · a distributable installer (`.pkg`/`.dmg`)
· self-contained runtime bundling (for .NET-less lab Macs) · an app icon. UI ports (Phase 33) and the
Windows-track teacher-mic follow-up remain independently tracked.
