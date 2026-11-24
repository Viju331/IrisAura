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
