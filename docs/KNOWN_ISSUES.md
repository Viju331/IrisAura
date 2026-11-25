# Known Issues

Open issues, limitations and unverified assumptions. Close an item by linking the evidence
(PROGRESS entry, ADR or test). Never paste biometric or credential data here.

| ID | Area | Issue | Impact | Next step / owner | Status |
|---|---|---|---|---|---|
| KI-001 | Build | Native project not yet compiled (no VS C++ tools). | Phase 0A acceptance | x64 build verified 2026-11-24 (PROGRESS Phase 0A) | Closed |
| KI-010 | Build | MSVC **ARM64** build tools not installed on the dev machine; ARM64 native build is skipped. | ARM64 LogonUI needs a native ARM64 DLL (BLUEPRINT §3.1) | Add the ARM64 build tools component before Phase 0C; run `scripts/build.ps1` | Open |
| KI-002 | Environment | Dev machine is Windows 11 **Home**: no Hyper-V for the disposable VM. | Blocks Phase 0C VM work | Use VMware Workstation Pro / VirtualBox, or upgrade to Pro ([VM_SETUP.md](VM_SETUP.md)) | Open |
| KI-003 | Brand | Simplified icon mark and tray mark are **drafts**; the full-colour mark is muddy at 16 px. | Release icons | Owner approval; consider a hand-tuned 16 px variant from a designer | Open |
| KI-004 | Brand | `build-brand-assets.ps1` not yet run (ImageMagick not installed); SVG rendering quality in ImageMagick's built-in renderer is unverified. ⚠ VERIFY | Icon quality | Install ImageMagick 7, run, inspect outputs | Open |
| KI-005 | Installer | Default WiX UI draws **black** text over the banner/dialog bitmaps, which conflicts with the dark-background-only logo rule (BLUEPRINT §0.3, §0.6). | Installer art | Design with the installer UI in Phase 9 (ADR 0003) | Deferred |
| KI-006 | Brand | No vector (SVG) master of the full logo symbol. ⚠ VERIFY (BLUEPRINT §0.3) | High-DPI art quality | Obtain from the designer | Open |
| KI-007 | Legal | Logo provenance and trademark search not yet recorded. | Release blocker | Fill in [BRAND_LICENSE.md](BRAND_LICENSE.md) | Open |
| KI-008 | Hotkeys | `Ctrl+Alt+Shift+I` registered successfully on DEV-01 (self-test); not yet checked against Office, browsers, VS Code, Teams on other machines. `Ctrl+Alt+Shift+T` arrives with timers (Phase 4). ⚠ VERIFY | Possible conflicts | Owner check on target machines | Open |
| KI-009 | Fonts | `Segoe UI Variable Text/Display` family names used for WPF; WPF's handling of variable-font instances is unverified. ⚠ VERIFY | Typography on Windows 11 | Visual check of the Island text; fallback is Segoe UI | Open |
| KI-011 | Island perf | Animations average 56–59 fps with 1–4 frames per transition at ~33 ms (one missed refresh) on DEV-01 at 14–27 % load from other apps. Target is no dropped frames (BLUEPRINT §8.8). | Visible micro-stutter possible | Re-measure on an idle machine; if still present, profile with Windows Performance Recorder (layered-window copy, 30 Hz poll/waveform timers) | Open |
| KI-012 | Island perf | Startup to first paint 775–3304 ms across runs (802 ms on a quiet machine); target ≤ 800 ms. Dominated by machine load and first-launch antivirus scan. | Logon experience | ReadyToRun + scheduled-task start in Phase 9; re-measure | Open |
| KI-013 | Island | Mixed-DPI / multi-monitor and top-taskbar behaviour only covered by unit tests; DEV-01 has one 100 % monitor. | Phase 1 acceptance | Owner test on a multi-monitor setup (HARDWARE_MATRIX) | Open |
| KI-014 | Island | Real foreground checks (hide over a full-screen app; stay over a maximised window) were inconclusive in some self-test runs because Windows' focus-stealing protection did not give the test window the foreground. Focus mode's `SetForegroundWindow` after `WM_HOTKEY` is unverified. ⚠ VERIFY | Test evidence | Manual checks (PROGRESS Phase 1 owner checks 3–4) | Open |
| KI-015 | Island | The idle pill is always visible (click-through). The optional 4 px "lip" auto-hide and S/M/L sizes (BLUEPRINT §8.1, §8.7) are not implemented yet. | Covers part of title bars visually | With the settings window (§8.7) | Open |
| KI-017 | Island perf | CPU with media + fake 30 Hz waveform measures 1.37–2.11 % (median of 3 samples) across 12 Phase 2 runs at 18–27 % machine load; target < 1.5 %. Each waveform update redraws and copies the whole layered window. | Battery / CPU budget | Measure on an idle machine; if still over, try a non-layered DWM-composited window or a smaller redraw surface (ADR 0004 alternatives) with the real waveform in Phase 3 | Open |
| KI-018 | Island | "Start timer" in the menu starts the debug timer; the real timer provider (persisted, hotkey, sound) is Phase 4. | Feature gap | Phase 4 | Open |
| KI-019 | Island | Menu item "Settings" is disabled until the Island settings window (§8.7). | Feature gap | §8.7 settings window | Open |
| KI-020 | Island | Native menus take the foreground while open (Windows requirement for correct dismissal) and hand it back afterwards. ⚠ VERIFY by hand that focus returns in Office, browsers and VS Code. | Focus | Owner check (PROGRESS Phase 2) | Open |
| KI-021 | Media | Spotify, Firefox, VLC, foobar2000 and real streaming sites not tested (not installed / owner accounts). | Phase 3 acceptance (Spotify) | Owner checks (PROGRESS Phase 3) | Open |
| KI-022 | Island perf | Working set 120–140 MB with real media (target < 120 MB; 80–100 MB without media). WinRT media APIs, NAudio and decoded images add memory. | RAM budget | Measure on a quiet machine; trim (release artwork when hidden, lazy NAudio) if still over | Open |
| KI-023 | Media | Browsers are one app for the "hide while in front" rule: with the browser in front its media hides whichever tab plays. Old UWP apps hosted in `ApplicationFrameHost` are not matched. | Behaviour detail | Owner feedback; per-tab detection is not available from GSMTC | Open |
| KI-024 | Media | Bringing the source app to the front (click) is not covered by automated tests (needs a real click for foreground rights). ⚠ VERIFY | Feature check | Owner check | Open |
| KI-025 | Media | Quiet audio gives a small waveform (level is a linear peak, √-shaped). | Visual | Tune with real music in owner tests | Open |
| KI-016 | Build | Installing Visual Studio 2026 removed .NET SDK 10.0.401 and added 11.0.100; `global.json` now rolls forward to the latest installed 10.0.x feature band (10.0.302 today). | Reproducibility | Install the latest .NET 10 SDK servicing release; keep `global.json` on 10.0.x | Open |

## Unverified blueprint items (⚠ VERIFY) carried forward

Tracked in BLUEPRINT; listed here when work depends on them: pre-logon camera access (Phase 0B),
IR stream access with Windows Hello/ESS (Phase 0B), WM_HOTKEY foreground permission (Phase 1),
WPF 120 fps (Phase 1), `NotificationChanged` in desktop apps (Phase 4), Bluetooth battery properties
(Phase 4), volume-flyout hiding per build (Phase 4), per-app SMTC support (Phase 3).
