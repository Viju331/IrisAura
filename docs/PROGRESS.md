# Progress

Evidence log, one entry per phase (BLUEPRINT §10). A phase is complete only when every acceptance
criterion has recorded evidence. Never record biometric or credential content here.

Status legend: ✅ done with evidence · ⏳ pending (owner noted) · ❌ failed · ➖ not applicable

---

## Phase 0A — Repository and development environment

**Branch:** `phase-0a` (merged to `main` 2026-11-24) · **Started:** 2026-11-24 · **Status:** ⏳ build criteria met; VM recovery drill, brand assets and owner actions still open.

### Scope delivered

- Solution `IrisAura.sln` (C# + native) and `IrisAura.Managed.slnf` (C# only, for the dotnet CLI), following BLUEPRINT §6 and the names in §0.2.
- Build configuration: `global.json` (SDK 10.0.401, latest patch; Microsoft.Testing.Platform), `Directory.Build.props` (C# only; net10.0-windows10.0.19041.0, C# 14, nullable, warnings as errors, lock files), `Directory.Packages.props` (all approved packages pinned), `nuget.config` (nuget.org only, source mapping), `native/IrisAura.Native.props` (C++20, /W4 /WX, static CRT, CFG, CET).
- Projects: Contracts (netstandard2.0, pipe names + protocol versions), Theme (Brand.xaml tokens §0.4, Glyphs.xaml §0.5), Island (WPF skeleton window, PerMonitorV2 manifest), Island.Providers, Island.Sdk (netstandard2.0), Island.Cli (`irisaura-island`), Face.Lab (WPF skeleton), Face.Core/Vision/Camera/Security (empty), Tools, inert native `IrisAura.Face.CredentialProvider` (no COM classes, no registration exports).
- Tests: `IrisAura.Island.Tests` — architecture rule (Island ⇸ Face), brand tokens vs §0.4, glyph geometry bounds, pipe names vs §0.2 and SID validation.
- Scripts: `build.ps1`, `build-brand-assets.ps1`, `new-dev-cert.ps1`, `generate-dependency-inventory.ps1`.
- Brand: draft simplified icon mark and monochrome tray mark (`assets/brand/source/*.svg`) — **awaiting owner approval**.
- Docs: this file, KNOWN_ISSUES, HARDWARE_MATRIX, SECURITY_REVIEW, THREAT_MODEL, RECOVERY, PRIVACY, ISLAND_SDK (draft), BRAND_LICENSE, VM_SETUP, DEPENDENCIES (generated), DECISIONS 0001–0003.
- Repository basics: README, LICENSE (proprietary), SECURITY, CONTRIBUTING, THIRD_PARTY_NOTICES, CLAUDE.md, .editorconfig, .gitattributes, .gitignore.

### Acceptance criteria

| Criterion (BLUEPRINT §10 Phase 0A) | Status | Evidence |
|---|---|---|
| Clean clone builds (C#) | ✅ | Fresh `git clone` of `phase-0a` into an empty folder, then `scripts/build.ps1 -Locked`: build 0 warnings / 0 errors, 34/34 tests passed (2026-11-24, .NET SDK 10.0.401, Windows 11 build 26300). |
| x64 native project builds | ✅ | `scripts/build.ps1` (Release) with Visual Studio Community 2026, MSVC 14.51.36231, Windows SDK 10.0.26100.0: C# build 0 warnings, 34/34 tests, native x64 passed (/W4 /WX) (2026-11-24). `dumpbin` on the DLL: exports only `DllCanUnloadNow` and `DllGetClassObject` (no `DllRegisterServer`); depends only on `KERNEL32.dll` (static CRT); Control Flow Guard, CET compatible, ASLR (high entropy) and NX enabled. |
| ARM64 native build (not a 0A criterion; needed from Phase 0C) | ⏳ owner | MSVC ARM64 build tools not installed, so `$(DefaultPlatformToolset)` is undefined for ARM64 (MSB8003). `build.ps1` reports the skip. KNOWN_ISSUES KI-010. |
| VM checkpoint/recovery demonstrated | ⏳ owner | Dev machine runs Windows 11 Home (no Hyper-V). Options and steps in [VM_SETUP.md](VM_SETUP.md). |
| Dependency inventory generated | ✅ | [DEPENDENCIES.md](DEPENDENCIES.md) via `scripts/generate-dependency-inventory.ps1` (22 package entries). |
| Model versions, hashes, licences pinned | ➖ | No models yet (Phase 6). `models/SHA256SUMS` and `models/LICENSES/` are in place. |
| Architecture rule enforced | ✅ | `ArchitectureTests`; negative check: adding a `IrisAura.Face.Core` reference to `IrisAura.Island.Providers` made the test fail, then reverted. |

### Owner actions to finish Phase 0A

1. ~~Install Visual Studio C++ tools~~ ✅ done (x64). Still to add: Visual Studio Installer → Modify → Individual components → **MSVC ARM64/ARM64EC build tools (latest)**, then re-run `./scripts/build.ps1`.
2. Install **ImageMagick 7**, run `./scripts/build-brand-assets.ps1`, and review the generated icons at 100/150/200 % scaling.
3. **Approve or change** the draft icon mark (`assets/brand/source/irisaura-mark.svg`) and tray mark.
4. Set up a disposable Windows 11 VM with a checkpoint and a second admin account ([VM_SETUP.md](VM_SETUP.md)); restore the checkpoint once and record it here.
5. Start **code-signing certificate procurement** (BLUEPRINT §12.4). Meanwhile, `./scripts/new-dev-cert.ps1` creates a test-only certificate.
6. Fill in [BRAND_LICENSE.md](BRAND_LICENSE.md) (who made the logos, under what terms) and run the trademark search (BLUEPRINT §0.7).
7. Decide on WiX licensing (Open Source Maintenance Fee) before Phase 9 (BLUEPRINT §12.2).

### Deviations from the blueprint (recorded as decisions)

- Tests use **xunit.v3 on Microsoft.Testing.Platform**, so `Microsoft.NET.Test.Sdk` and `xunit.runner.visualstudio` are not needed ([ADR 0002](DECISIONS/0002-build-and-test-infrastructure.md)).
- C++ settings live in `native/IrisAura.Native.props`, imported after `Microsoft.Cpp.props`, instead of `native/Directory.Build.props`. `Directory.Build.props` is evaluated before the C++ defaults, which would override it ([ADR 0002](DECISIONS/0002-build-and-test-infrastructure.md)).
- WiX banner/dialog bitmaps are deferred to Phase 9 ([ADR 0003](DECISIONS/0003-brand-asset-pipeline.md)).

---

## Phase 1 — IrisAura Island shell and motion

**Branch:** `phase-0a` (owner asked to continue on the same branch) · **Date:** 2026-11-24 · **Status:** ⏳ implemented and tested on DEV-01; acceptance partly met — see table. Owner checks listed below.

### Scope delivered (BLUEPRINT §8.2–§8.4, §10 Phase 1)

- **Shell** (`Shell/IslandWindow`): transparent layered window created once at 480 × 250 DIP; topmost tool window; `WS_EX_NOACTIVATE` + `WM_MOUSEACTIVATE → MA_NOACTIVATE`; click-through by layered alpha + `WS_EX_TRANSPARENT` toggled by a 30 Hz cursor poll with a 6 DIP hot zone; idle pill never blocks clicks.
- **Placement** (`Shell/Placement`, `Win32Shell`): top-centre of the work area (below a top taskbar/app bar), per-monitor DPI v2, primary or follow-cursor monitor (500 ms debounce); re-placed on `WM_DISPLAYCHANGE`, `WM_DPICHANGED`, `WM_SETTINGCHANGE`, `SystemEvents.DisplaySettingsChanged` and work-area changes.
- **Topmost / full screen** (`FullscreenPolicy`): topmost re-asserted on `EVENT_SYSTEM_FOREGROUND`; hides (150 ms fade) for `QUNS_BUSY` / `QUNS_RUNNING_D3D_FULL_SCREEN` / `QUNS_PRESENTATION_MODE` or a foreground window covering the Island's monitor (desktop/shell, Island windows and maximised-with-title-bar windows excluded); "show over full screen" option.
- **Motion** (`Motion/*`): springs (k 300, c 30 collapse / 22 expand) driven by real frame time; width leads height by 40 ms; outgoing content fades in 80 ms; incoming fades in after 60 % of the size change with 0.92 → 1 scale and upward slide; split bubble pinches off on springs; ±8 DIP / 3-cycle / 300 ms failure shake; rendering hooked only while animating.
- **Reduced motion**: follows Windows "Animation effects" (`SPI_GETCLIENTAREAANIMATION`) or a debug override; size snaps, content cross-fades 150 ms, shake becomes an `Aura.Error` border flash.
- **Hover / click** (`HoverIntent`): hover 150 ms expands; leave 400 ms collapses; click pins / unpins; bubble click swaps.
- **Keyboard & accessibility**: `Ctrl+Alt+Shift+I` (no Windows key) enters keyboard focus mode (Tab/Enter/Space, Esc returns focus to the previous window); hotkey conflicts are reported, not fought; pill has a UI Automation peer and announces content via LiveRegionChanged.
- **Fake activities + debug panel** (`Diagnostics/*`): media (waveform, transport), timer (ring, pause / +1 min / cancel), notification banner (4 s, held while hovered), timer + media split; debug panel with options and live CPU / RAM / frame stats.
- **Self-test** (`--selftest report.json`, `--perf report.json`), single instance, `--debug`, `--quit`.
- **Theme**: media glyphs (play / pause / previous / next) added to `IrisAura.Theme`.
- **Tests**: 83 unit tests (was 34): springs, morph ordering and cross-fade, shake, layout vs §8.2 table, hover intent, placement (100 / 150 / 200 %, top taskbar, negative coordinates), full-screen policy, frame stats, presentation rules, glyph bounds, architecture rule.

### Acceptance criteria (BLUEPRINT §10 Phase 1)

| Criterion | Status | Evidence |
|---|---|---|
| No focus theft during typing | ✅ automated / ⏳ manual | Self-test: foreground window unchanged and never the Island across all 9 transitions (9/9 pass); `WS_EX_NOACTIVATE` present throughout. Manual typing test in VS Code, Word and a browser address bar still to do (owner). |
| Correct behaviour on mixed-DPI multi-monitor | ⏳ partial | Unit tests cover 100/150/200 %, negative coordinates, top-docked work area. Self-test on DEV-01 (1 monitor, 100 %): placed exactly at top-centre of the work area; re-placed after `WM_DISPLAYCHANGE` and work-area change. **No multi-monitor / mixed-DPI hardware available** — owner test needed. |
| No overlay in configured full-screen scenarios | ✅ / ⏳ | Policy correctly classified a borderless monitor-covering window (hide) and a maximised titled window (no hide); unit tests cover shell states and edge cases. Real foreground test: hid over a borderless full-screen window in run 1; later runs inconclusive because Windows withheld the foreground from the test window. Games, YouTube full screen and PowerPoint slideshow still to test by hand (BLUEPRINT §8.12). |
| Animation performance measured and recorded | ✅ measured · ❌ target "no dropped frames" not met | See performance table. Averages 17.0–18.0 ms (56–59 fps); 1–4 frames per transition at ~33 ms (one missed refresh) with the machine at 14–27 % load from other apps. |

### Performance on DEV-01 (final run, `docs/evidence/phase-1-selftest-2026-11-24.json`)

Release build, JIT (no ReadyToRun), Windows 11 build 26300, 4 logical processors, 1920 × 1080 @ 100 %.

| Metric (BLUEPRINT §8.8) | Target | Measured | Result |
|---|---|---|---|
| Idle CPU | < 0.3 % | median 0.273 % (machine load 14 %) | ✅ |
| CPU with media + waveform (fake 30 Hz) | < 1.5 % | median 1.289 % (machine load 17 %) | ✅ |
| RAM | < 120 MB | 80.3 MB working set, 34.6 MB private | ✅ |
| Startup to first paint | ≤ 800 ms | 802 ms (range 775–3304 ms across runs, driven by machine load and first-launch antivirus scan) | ❌ borderline |
| Animation | 60 fps, no dropped frames | 56–59 fps average; p95 16.7–33 ms; 1–4 dropped per transition | ❌ |
| Hover → expand | ≤ 200 ms | 150 ms by design + first frame | ✅ by design |

Earlier runs (kept for comparison): with `DropShadowEffect`, media CPU 4.8–9.2 % and startup 3.0–3.3 s (`docs/evidence/phase-1-selftest-2026-11-24-run1-blur-shadow.json`) — the reason for the vector shadow ([ADR 0004](DECISIONS/0004-island-shell-rendering-and-interaction.md)). Under 68–100 % machine load, single CPU samples varied from 0.6 % to 6 %; the self-test therefore uses a 5 s warm-up and the median of three 10 s samples.

### Owner checks to finish Phase 1

1. Run `IrisAura.Island.exe --selftest island.json` (Release build) **with other apps closed** and record the numbers here.
2. Type in VS Code, Word and a browser address bar while hovering / clicking the Island: focus must never move.
3. Press `Ctrl+Alt+Shift+I`: the Island must take keyboard focus; Tab / Enter / Esc work; Esc returns to the app you were in.
4. Full screen: YouTube full screen, PowerPoint slideshow, a DirectX game (exclusive and borderless) — the Island must hide.
5. If you have a second monitor: different scaling on each, taskbar on top (Windows 10) or an app bar, connect / disconnect while running, "Follow the cursor's monitor" on.
6. Turn off Windows *Settings → Accessibility → Visual effects → Animation effects*: the Island must stop springing and only cross-fade.
7. Narrator: hover / pin the Island; Narrator should read the activity.

Run it with `IrisAura.Island.exe --debug` (debug panel), quit with the panel's button or `IrisAura.Island.exe --quit`.

### Deviations (recorded as decisions)

- Vector `PillShadow` instead of `DropShadowEffect`; window 480 × 250 DIP; idle pill click-through; `LibraryImport` instead of CsWin32 ([ADR 0004](DECISIONS/0004-island-shell-rendering-and-interaction.md)).
- `global.json` now selects the latest installed .NET 10 SDK (≥ 10.0.300) because installing Visual Studio 2026 removed SDK 10.0.401 ([ADR 0002](DECISIONS/0002-build-and-test-infrastructure.md)).
- Settings window and persistence (§8.7) are not part of Phase 1; options live in memory with blueprint defaults and are adjustable in the debug panel.
- Fixed: Phase 0A lock files still listed `IrisAura.Face.Core` under `IrisAura.Island` (left over from the architecture-test check); regenerated.

---

## Phase 2 — IrisAura Island activity engine

**Branch:** `main` (owner decision: work on `main`) · **Date:** 2026-11-24 · **Status:** ✅ acceptance criteria met (unit tests); integration verified on DEV-01; performance targets from Phase 1 still open.

### Scope delivered (BLUEPRINT §8.5, §10 Phase 2)

- **Activity model** (`Engine/IslandActivity`): §8.5 record plus owner, expiry, pause, sequence, optional rich view; defaults from the §8.5 priority table; custom/SDK priority capped at 60.
- **ActivityManager**: start / partial update / end / end-with-final-text; provider ownership, duplicate ids, stale (sequenced) updates, TTL expiry (transients from last update), provider-wide end.
- **PriorityArbiter**: main / bubble / queue; transient preemption and restoration; waiting transients; paused media → queue after 30 s; user preference (swap, wheel cycle); quiet mode (user or Windows quiet time) suppresses notifications only; privacy indicators kept out of the pill; `NextChangeAt` for time-driven re-arrangement.
- **ActivityProviderHost**: provider isolation (start / action / reported failures disable only that provider and remove its activities; restartable), thread-safe sink marshalled to the UI thread with order-preserving coalescing.
- **IslandController**: arbiter → presentation, timed refresh, swap / cycle / middle click / buttons routed to the owning provider, quiet mode, hide for 1 hour, persisted state.
- **Generic activity view + templates**: compact, expanded (title, subtitle, progress, buttons), banner, bubble — used by any activity without a custom view (SDK, HUDs, calls, downloads).
- **Tray icon + native menus**: right-click the pill or the tray icon → Quiet mode, Hide for 1 hour / Show, Start timer, Settings (disabled until §8.7), Debug panel (debug), Quit. Focus returns to the previous window after the menu.
- **Window**: mouse wheel, middle click, right click events; user-hidden state separate from full-screen hiding; hotkey shows a hidden Island; adaptive cursor polling (30 Hz near the Island, 10 Hz far away).
- **Startup**: tray icon and providers start after the first frame.
- **Persistence**: quiet mode and hide-until in `%LocalAppData%\IrisAura\island-state.json` (no content).
- **Logging**: Serilog to `%LocalAppData%\IrisAura\logs` (no user content).
- **Debug provider + panel**: media, timer (incl. 12 s timer promoted to 85), call, download, SDK-style custom, notification, volume HUD, charging, swap, cycle, hide/show, simulated provider failure and restart; live layout and provider states.

### Acceptance criteria (BLUEPRINT §10 Phase 2)

| Criterion | Status | Evidence |
|---|---|---|
| Priority ties | ✅ | `PriorityArbiterTests`: start order then id; updates do not reorder ties; transient ties newest first |
| Transient interruption / restoration | ✅ | Unit: persistent layout kept underneath and restored; low-priority transient still interrupts. Self-test: banner took the pill and the split layout returned after expiry |
| Expiry | ✅ | `ActivityManagerTests`: transient TTL from last update; persistent TTL from start; end-with-final-text then removal; arbiter `NextChangeAt` |
| Cancellation | ✅ | End, end-all-from-provider, provider stop; self-test timer cancel path via debug provider |
| Duplicate ids | ✅ | Same provider → replace (keeps start time); other provider → `IdConflict` for start/update/end |
| Stale updates | ✅ | Sequenced updates ≤ last ignored; updates after end/while ending ignored (no resurrection) |
| Provider failures | ✅ | `ProviderHostTests` (start throws, reported failure, action throws, calls after fault ignored, restart). Self-test: debug provider failure → faulted, its activities removed, Island idle, restart works |

Tests: **138 unit tests** (was 83), all passing; build 0 warnings; native x64 builds.

### Self-test on DEV-01 (`docs/evidence/phase-2-selftest-2026-11-24.json`)

38 pass, 12 fail, 2 inconclusive. All functional checks pass: arbiter placement, transient takeover and restore, swap, tray icon, quiet mode, provider failure isolation and restart, reduced motion, click-through, placement, display/work-area changes, focus never taken (10/10).

| Metric (BLUEPRINT §8.8) | Target | Phase 2 measurements (machine load) | Result |
|---|---|---|---|
| Idle CPU | < 0.3 % | 0.117 % final run (18 %); 0.12–0.51 % across 12 runs | ✅ final / borderline |
| CPU media + waveform | < 1.5 % | 1.521 % final run (22 %); 1.37–2.11 % across 12 runs | ❌ borderline (Phase 1: 0.66–3.5 %) |
| RAM | < 120 MB | 87 MB working set | ✅ |
| Startup to first paint | ≤ 800 ms | 1757 ms final; 1288–2361 ms across runs | ❌ (KI-012) |
| Animation | 60 fps, no drops | 54–58 fps average; 1–3 frames > 25 ms per transition | ❌ (KI-011) |

### Owner checks

1. Right-click the pill (with an activity showing) and the tray icon: the menu appears, focus returns to your app afterwards, Quiet mode / Hide for 1 hour / Quit work.
2. With two activities showing, click the bubble (swap) and scroll the wheel over the pill (cycle).
3. Hide for 1 hour, then press `Ctrl+Alt+Shift+I`: the Island comes back.
4. Light and dark taskbar: the tray icon is visible in both.
5. Everything from the Phase 1 owner checks, plus a quiet-machine `--selftest`.

### Deviations (recorded in [ADR 0005](DECISIONS/0005-activity-engine.md))

- Tray icon added (needed to reach the menu while idle or hidden). Settings item disabled until §8.7.
- "Start timer" uses the debug timer until the timer provider (Phase 4).
- `Navigation` priority 55 (not in the §8.5 table).
- Queue reachable by mouse wheel / cycle; the "swipe in expanded view" gesture is not implemented (mouse-first desktop).

---

## Phase 3 — IrisAura Island media and waveform

**Branch:** `main` · **Date:** 2026-11-25 · **Status:** ✅ implemented and tested with Media Player, Edge and Chrome on DEV-01; Spotify / Firefox / streaming sites still to test by the owner.

### Scope delivered (BLUEPRINT §8.6.1–§8.6.2, §10 Phase 3)

- **Media provider** (`IrisAura.Island.Providers/Media`): GSMTC session discovery, metadata, artwork (cached when present), playback status, controls, timeline; session selection (playing → system current → paused; switcher choice); reconnect with back-off; de-duplicated, debounced publishing.
- **Transport controls:** play/pause, previous, next, seek (click the bar), middle-click play/pause; disabled when the app does not allow them.
- **Timeline interpolation:** position advances between the app's irregular reports, respects playback rate, clamps; bar hidden when the app reports no duration.
- **Album art and tint:** decoded off the UI thread, dominant colour (excluding near-black/white, brightened for the black pill) tints the waveform; app icon directly on the pill when there is no art (owner request).
- **Source app:** name and icon (packaged: AppInfo; unpackaged: exe description and shell icon); click on the compact pill or the app badge brings the app to the front.
- **Waveform:** the app's own audio-session peak (NAudio CoreAudio), device-level fallback, attack/release smoothing, flat when paused; timers only while visible.
- **Hide while the app is in front (owner request):** a media activity hides while its app is the foreground app and reappears when it is minimised or another app comes forward.
- **Architecture:** provider contracts moved to `IrisAura.Island.Providers` (§6 layout).
- **Diagnostics:** `--media-probe report.json` (per-app evidence, no titles); debug panel shows the app in front and what it hides.

### Acceptance criteria (BLUEPRINT §10 Phase 3)

| Criterion | Status | Evidence |
|---|---|---|
| Spotify | ⏳ owner | Not installed on DEV-01 |
| Multiple browsers | ✅ Edge, Chrome · ⏳ Firefox | Table below; Firefox not installed |
| At least one local player | ✅ Media Player | Table below; the Island showed the real session (self-test info check: "Microsoft.ZuneMusic…, playing") |
| Document per-app gaps | ✅ | Table below and KNOWN_ISSUES |
| No claim that all players publish SMTC | ✅ | README / BLUEPRINT wording unchanged; per-app table only lists tested apps |

### Per-app results on DEV-01 (quiet 440 Hz test tone; `docs/evidence/phase-3-probe-*.json`)

| App (session id) | Shown | Title / artist / album | Artwork | Timeline / seek | Next / previous | Own audio level |
|---|---|---|---|---|---|---|
| Media Player (`Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic`) | ✅ | ✅ / — / — (WAV has no tags) | — (no tags) | ✅ 40 s / ✅ | — (single file) | ✅ |
| Edge (`MSEdge`) | ✅ | ✅ / ✅ / — | ❌ page artwork from `file://` not loaded | ✅ / ✅ | — | ✅ |
| Chrome (`Chrome.<per-install id>`) | ✅ | ✅ / ✅ / ✅ | ⚠ Chrome's own logo (page artwork from `file://` not loaded) | ✅ / ✅ | — | ✅ after the `Chrome.` prefix fix |

### Tests and measurements

- **201 unit tests** (was 138): timeline, selection, tint, app identity (incl. Chromium prefixes), waveform smoothing, redundant-update detection, provider publish/pause/end/switcher/actions/reconnect/fault, artwork decode and tint on real image bytes, in-front rule and matching.
- Self-test (`docs/evidence/phase-3-selftest-2026-11-25.json`): 39 pass, 11 fail (startup 1830 ms; dropped frames), 2 inconclusive. Idle CPU 0.039 %, fake waveform 1.131 %.
- Real Chrome media + live waveform, Island measured from outside over 20 s: **1.445 % and 1.521 %** CPU (machine load 22 % / 44 %), working set 120–127 MB. Before caching images: 3.4–9.3 % ([ADR 0006](DECISIONS/0006-media-and-waveform.md)).

### Owner checks

1. Spotify desktop: title, art, controls, waveform, click opens Spotify.
2. YouTube / YouTube Music / JioSaavn in Chrome and Edge; Firefox if installed; VLC (needs SMTC support enabled).
3. Two apps playing: the switcher ("Also: …") in the expanded view.
4. **The new rule:** play music in Spotify or a browser, keep the app in front → nothing on the Island; minimise it (or click another app) → it appears; click the pill → the app comes forward and the pill hides it again.
5. Headphones plugged in / unplugged while playing: the waveform keeps moving.

### Deviations (ADR 0006)

- One media activity plus an app switcher instead of one activity per session.
- "Hide while the app is in front" added at the owner's request (rule: foreground app, not "minimised only").
- No background colour behind the app icon (owner request).

---

## Phase 4 — IrisAura Island system providers

**Branch:** `main` · **Date:** 2026-11-25 · **Status:** ✅ volume, brightness, power, Bluetooth, privacy indicators, timers and downloads implemented and tested on DEV-01 (live where automation can trigger them); call detection (beta, off by default) implemented; **notifications deferred** pending owner approval of the sparse-package / dev-certificate step (KI-026).

### Scope delivered (BLUEPRINT §8.6.3–§8.6.11, §10 Phase 4; ADR 0007)

| Provider (id) | Source | Shows | Unavailable when |
|---|---|---|---|
| Volume (`volume`) | CoreAudio endpoint volume (NAudio), re-subscribes on default-device change | 1.5 s HUD: speaker / muted glyph, level bar, % | no output device |
| Brightness (`brightness`) | WMI `WmiMonitorBrightnessEvent` | 1.5 s HUD: sun glyph, bar, % | no WMI brightness (desktop / external monitor) |
| Charging and battery (`power`) | `PowerManager` events | plug-in: bolt, %, bar (2.5 s); low battery at 20 % and 10 % once per discharge (5 s, alert colour) | no battery |
| Bluetooth devices (`bluetooth`) | `DeviceWatcher` on connected classic + LE endpoints | "&lt;name&gt; Connected" for 3 s, glyph by device class; battery % when Windows exposes it; classic + LE announced once | no adapter |
| Microphone and camera indicators (`privacy`) | consent store (registry watch) + CoreAudio capture-session notifications for the microphone | violet (mic) / cyan (camera) dots beside the Island; app list in the menu | — |
| Timers (`timers`) | the Island's own; menu, `Ctrl+Alt+Shift+T` | ring + `mm:ss`; expanded: pause/resume, +1 min, cancel; priority 85 in the last 10 s; "Timer done" banner + sound + shake; survives restarts | — |
| Downloads (`downloads`) | `FileSystemWatcher` on the Downloads known folder | file name + bytes so far (no %: total unknown), "Done ✓"; click opens the folder | no Downloads folder |
| Call detection, beta (`calls`, **off by default**) | known call app holds the mic **and** plays audio | call glyph, app, elapsed time, "call detection (beta)"; ends 3 s after the mic is released | — |

Also: an **Activities** submenu (tray and right-click) switches each provider on/off, retries unavailable or failed
ones, and persists the choice in `island-settings.json`; `ProviderState.Unavailable` and
`IActivitySink.ReportUnavailable` (missing hardware is not an error); `ActivityTone` (brand-token colours for
success/alert); 15 new original glyphs (no Bluetooth logo); `--system-probe` diagnostics.

### Acceptance criteria (BLUEPRINT §10 Phase 4)

| Criterion | Status | Evidence |
|---|---|---|
| Volume, brightness, power, Bluetooth, privacy indicators, timers, downloads as independent providers | ✅ | One class each in `IrisAura.Island.Providers`; registered separately; host tests |
| Each provider can be independently disabled/restarted | ✅ | Activities menu; `ProviderHostTests` (disable removes only its activities, StartAll skips disabled, enable restarts, unavailable → retry, stale reports ignored) |
| Missing API/device support degrades gracefully | ✅ unit + design · ⏳ on a desktop / no-battery device | `ReportUnavailable` path tested; DEV-01 has every device, so the path has not run on real hardware |
| Notification content not persisted by default | ✅ (nothing collected) | Notifications not implemented (KI-026); PRIVACY.md |
| Notifications only after package identity, permission UX, privacy handling and build compatibility are proven | ⏳ deferred | Needs owner approval (trust-store change) |
| Call detection labelled heuristic/beta; never from mic use alone | ✅ | Off by default; label in the activity; `CallDetector` tests (mic alone never starts a call) |

### Evidence on DEV-01 (Windows 11 26H2, build 26300; laptop with battery, WMI brightness, Realtek Bluetooth)

- `--system-probe` (`docs/evidence/phase-4-system-probe-2026-11-25.json`, flags and counts only): the brightness HUD
  fired **66 ms** after a 1 % change (restored to 70 %); the volume HUD **211 ms** (restored); the microphone opened by
  the probe was seen by the monitor in **0.37 s** and its release in **0.26 s**; downloads: the Chromium rename
  sequence, the Firefox `.part` sequence and a cancelled download behaved as expected.
- **Finding:** the consent store on this build has not been written since 2026-10-12 for any app, and was not written
  when the probe used the microphone; hence the capture-session source (ADR 0007, KI-028).
- Live Island (screenshots inspected, not committed): a timer restored from `island-timer.json` after a restart counted
  down, entered the last-10 s state, ended with the "Timer done · 3 min timer" banner (alert glyph) and deleted its
  file; the volume HUD showed "Muted" with the crossed speaker (output muted at 100 %); the violet microphone dot
  appeared beside the timer pill while the probe held the microphone and cleared after release.
- Bluetooth: the Windows battery property is present for both paired devices (30 % / 90 %); none was connected during
  the probe, so the live announcement is an owner check.
- Idle CPU with every provider running, measured from outside (3 × 20 s, two runs): **0.04–0.18 %** (machine load
  15–18 %); with the privacy provider off 0.08–0.22 %. The first version read audio capture sessions once a second and
  cost 0.15–0.2 % on its own (self-test 0.312 % / 0.429 %); it is now event-driven (session-created and state
  notifications). The power provider's first WinRT call (~0.5 s) was moved off the UI thread.

### Tests

- **275 unit tests** (was 201): HUD change detection and specs; battery thresholds per discharge cycle, at startup and
  on plug-in; Bluetooth class-of-device and LE appearance mapping, battery text, classic + LE de-duplication;
  consent-store parsing and capture-session merge; privacy provider start/end; indicators never take the pill; call
  heuristic (corroboration, 3 s grace, packaged apps, provider start/end); timer format, pause/resume/+1 min, 10 s
  promotion, finish banner, persistence across restarts, missed while closed; download sequences (Chromium, Firefox,
  cancel, limit, sizes); settings round trip; provider enable/disable/unavailable; 15 new glyphs fit the 24-unit grid.
- Self-test (`docs/evidence/phase-4-selftest-2026-11-25.json`): **44 pass / 11 fail / 2 inconclusive**. New checks pass:
  no provider failed at startup (8 running, calls switched off), **idle CPU with all system providers running 0.156 %**,
  timer shortcut registered, mic indicator dot drawn beside the pill and removed. Idle CPU (providers stopped) 0.039 %.
  Fails are the known performance items only: startup 807 ms (target 800, KI-012) and 1–2 dropped frames per
  transition (KI-011). The two full-screen checks stay inconclusive (KI-014).

### Owner checks

1. **Bluetooth:** with the Island running, connect your AirPods (and a mouse or keyboard if you have one): "&lt;name&gt;
   Connected" with the headphones glyph; the battery % should appear within ~2 s if Windows shows it in Settings.
2. **Charging:** unplug and plug in the charger: bolt, %, filling bar. Let the battery reach 20 % on battery: one alert banner.
3. **Volume / brightness keys:** the Island HUD appears (Windows' own flyout still appears too, KI-027).
4. **Camera:** open the Camera app or a Teams video call: does the cyan dot appear? (It may not on this build, KI-028.)
   Microphone in Teams / WhatsApp / Sound Recorder: the violet dot appears; right-click the Island to see which app.
5. **Timer:** right-click → Start timer → 1 min; press `Ctrl+Alt+Shift+T` to pause/resume; hover for pause / +1 min /
   cancel; quit and restart the Island while it runs: it continues. At the end: banner, sound, shake.
6. **Downloads:** download a file in Edge, Chrome or Firefox: name + MB, then "Done ✓"; a click opens the Downloads folder.
7. **Calls (beta):** right-click → Activities → tick "Call detection (beta)", then join a Teams or WhatsApp call.
8. **Activities menu:** switch a provider off and on; the choice survives a restart.

### Deviations (ADR 0007)

- The microphone indicator also uses CoreAudio capture sessions (consent store not updated on build 26300).
- Indicator dots use brand colours (violet / cyan) and sit just outside the pill; the app list is in the menu until Phase 4B.
- Notifications and volume-flyout hiding not built (KI-026, KI-027).
- Downloads show bytes, with no animated indeterminate bar (CPU budget).
- Browser calls are not detected (would need window titles).

---

## Phase 4B — IrisAura Island expanded panel redesign

**Branch:** `main` · **Date:** 2026-11-25 · **Status:** ✅ implemented and tested on DEV-01 (self-test with the real camera); drag-and-drop with real apps, real-media panel CPU and AirPods banner are owner checks.

### Scope delivered (BLUEPRINT §8.13A, §8.6.12; ADR 0008)

- **Dashboard panel** (640 × 224, floating pill shape kept) replaces the old expanded views. Opens by hovering the
  Island in any state except a banner (including the idle pill), from the tray menu ("Open panel"), or by dragging
  files onto the Island.
- **Island tab:** the "now" card shows the main activity's expanded view — a new **media card** (large art with the
  app badge, title / artist, live waveform, seek bar, controls), or the timer / call / download view; "Nothing
  playing" when idle. A **calendar strip** (month, year, five days with today highlighted; dates only). A **camera
  mirror** card (round, mirrored live preview; off until switched on).
- **Tray tab:** the **file tray** (§8.6.12): drop files or folders on the Island to keep shortcuts; click to open; drag
  out to any app; remove one or "Clear tray"; persisted in `island-shelf.json` (paths only, max 30).
- **Bluetooth "connected"** banner in the new design: device glyph in a battery ring (faint full ring when the level is
  unknown) with a scale-in.
- **Window** grows to 740 × 290 only while the panel is open and shrinks back after it closes; the waveform runs at
  15 Hz inside the panel.
- Self-test additions: panel checks, `--snapshots dir` (renders of the Island's own visual), `--test-camera`.

### Acceptance criteria (BLUEPRINT §10 Phase 4B)

| Criterion | Status | Evidence |
|---|---|---|
| Tabs (Island / Tray), large media card, calendar strip, file tray, opt-in camera mirror, Bluetooth device activity in the new design | ✅ | `docs/evidence/phase-4b-snapshots/*.png` (panel-idle, panel-media, panel-tray, device-banner) |
| §8.8 budgets still met (measured) | ✅ CPU/RAM · ⚠ frames as before | Idle 0.117 % (providers stopped) / 0.117 % (all providers); media + waveform 1.055 %; **panel open 0.82 %**; working set 116 MB. Transitions keep the known 1 dropped frame each (KI-011); startup 967 ms (KI-012) |
| Focus / click-through / full-screen tests still pass | ✅ focus, click-through · inconclusive full screen (as before, KI-014) | No focus taken on panel open/close; click-through restored after closing; window placement checks pass at both sizes |
| Camera mirror off by default; camera released when the panel closes | ✅ | Self-test: "off by default: no camera opened"; with `--test-camera`: live in 0.8 s, camera dot on, released on the Tray tab and on close, **exclusive camera access granted afterwards** |
| No calendar or camera access without user consent | ✅ | Calendar: dates only, no API calls; camera only after the user's "Turn on" |

### Measurements and fixes during the phase

- First panel run: **4.4 %** CPU with the panel open on the media card. Two causes: the self-test's fake media
  re-laid out its timeline text at 30 Hz (the real media view does it at 4 Hz), and a 30 Hz waveform copies the
  whole, 1.8× larger, layered surface each frame. Fake aligned with the real view and the panel waveform set to 15 Hz:
  **0.82 %**.
- Closing the panel dropped **7** frames; caching the outgoing content as a bitmap while it fades: **1**.
- Tab highlight did not show (bool vs string trigger) and two of three tray icons did not load (shell icon off a COM
  thread); both fixed and visible in the snapshots.

### Tests

- **287 unit tests** (was 275): panel window fits with overshoot; pill keeps its screen position when the window
  grows (100 % and 150 %); file tray order, duplicates, missing files, drive roots, limit, prune, persistence, corrupt
  file; calendar strip centring, month end, accessible names, user language; panel visibility rules (now card only
  while open on the Island tab, replaced while open, Tray tab hides it); mirror off by default and never opens the
  camera while off; mirror setting persisted; device view battery ring.
- Self-test (`docs/evidence/phase-4b-selftest-2026-11-25.json`, with `--test-camera`): 62 pass / 13 fail / 2 inconclusive.
  All panel, camera, focus and CPU checks pass; fails are startup (967 ms) and one dropped frame per transition.

### Owner checks

1. Hover the idle pill: does opening the panel from idle feel right, or should idle stay click-through? (KI-015)
2. Play music, hover the Island: the media card; use the controls and the seek bar; leave it open a minute (KI-036).
3. Drag a file from Explorer, a browser download bar or Outlook onto the Island → Tray tab; drag it out into an email
   or folder; click to open; remove; Clear tray. Restart the Island: the tray is kept (KI-037).
4. Camera mirror: Turn on, close the panel (camera light goes off), reopen (it starts again); during a Teams video call
   the mirror must not interrupt the call (KI-038). Turn off.
5. Connect your AirPods: the new device banner with the battery ring if Windows reports a level.

### Deviations (ADR 0008)

- Hovering the idle pill opens the panel (the idle pill takes clicks only while the cursor is on it).
- The panel replaces the old expanded views; the media card is the panel's now card.
- Calendar shows dates only (events need KI-026's package identity and consent).
- The panel's waveform runs at 15 Hz (CPU budget).
- Microphone/camera app list still in the menu, not the panel (KI-034).
