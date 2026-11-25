# Hardware Matrix

What was tested on which device (BLUEPRINT §3.6, §10, §11.2). One row per device × Windows build ×
test context. A single successful device is not proof for a device class. Do not record serial
numbers, user names or any biometric data.

## Target minimum matrix (BLUEPRINT §11.2)

| Class | Needed | Have? | Device |
|---|---|---|---|
| Windows Hello IR laptop with ESS | 1 | ⏳ | |
| Windows Hello IR laptop without ESS | 1 | ⏳ | |
| RGB-only laptop (Intel) | 1 | ⏳ | |
| RGB-only laptop (AMD) | 1 | ⏳ | |
| External USB webcam | 1 | ⏳ | |
| Snapdragon ARM64 laptop | 1 | ⏳ | |

## Devices

| ID | Model | CPU / Arch | Windows edition + build | Role |
|---|---|---|---|---|
| DEV-01 | Developer workstation | x64 | Windows 11 Home, build 26300 | Build machine only. **Never** a credential-provider test device. |

## Camera results (Phase 0B)

| Date | Device | Windows build | Camera (VID:PID) | Driver | Kinds (RGB/IR/Depth) | Hello / ESS | Context (user / elevated / LocalSystem / locked / pre-logon) | Frames received? | Notes / evidence |
|---|---|---|---|---|---|---|---|---|---|
| | | | | | | | | | |

## Island results (Phases 1–5)

| Date | Device | Windows build | DPI / monitors | Test | Result | Notes / evidence |
|---|---|---|---|---|---|---|
| 2026-11-24 | DEV-01 | 26300 | 1 × 1920×1080 @ 100 %, taskbar bottom | Phase 1 self-test (Release, JIT) | 31 pass / 10 fail / 2 inconclusive | Fails: startup 802 ms (target 800), 1–4 dropped frames per transition. Machine load 14–27 % from other apps. `docs/evidence/phase-1-selftest-2026-11-24.json` |
| 2026-11-24 | DEV-01 | 26300 | same | Focus theft (automated) | Pass | Foreground unchanged across 9 transitions |
| 2026-11-24 | DEV-01 | 26300 | same | Click-through (WindowFromPoint) | Pass | Alpha-0 pixels pass clicks without `WS_EX_TRANSPARENT`; shadow pixels need the toggle |
| 2026-11-24 | DEV-01 | 26300 | same | Full screen (borderless covering window) | Pass (run 1), inconclusive later | Later runs: Windows withheld foreground from the probe window |
| 2026-11-24 | DEV-01 | 26300 | same | Phase 2 self-test (activity engine) | 38 pass / 12 fail / 2 inconclusive | All engine checks pass (arbiter, transient restore, swap, tray, quiet mode, provider isolation/restart). Fails: performance only. `docs/evidence/phase-2-selftest-2026-11-24.json` |
| 2026-11-25 | DEV-01 | 26300 | same | Phase 3 media: Media Player, Edge, Chrome (test tone) | Pass (per-app gaps documented) | `docs/evidence/phase-3-probe-*.json`; Chrome uses `Chrome.<hash>` session id |
| 2026-11-25 | DEV-01 | 26300 | same | Island CPU with real Chrome media + live waveform | 1.45–1.52 % | After caching icon/artwork (was 3.4–9.3 %); WS 120–127 MB |
| 2026-11-25 | DEV-01 | 26300 | same | Phase 3 self-test | 39 pass / 11 fail / 2 inconclusive | Fails: startup, dropped frames. `docs/evidence/phase-3-selftest-2026-11-25.json` |
| 2026-11-25 | DEV-01 | 26300 | same | Phase 4 system probe (`--system-probe --nudge-volume --nudge-brightness --probe-mic`) | Pass (see notes) | Battery present; WMI brightness HUD 66 ms; volume HUD 211 ms, restored; mic seen in 0.37 s, release 0.26 s (via capture-session notifications; the consent store recorded nothing, KI-028); download sequences simulated in a temp folder. `docs/evidence/phase-4-system-probe-2026-11-25.json` |
| 2026-11-25 | DEV-01 | 26300 | same | Live Island: timer restored from file after restart, last-10 s, "Timer done" banner; volume HUD; mic dot appears and clears | Pass | Screenshots inspected (not committed: they show the desktop) |
| 2026-11-25 | DEV-01 | 26300 | same | Bluetooth battery property on paired devices (PnP, `{104EA319…} 2`) | Present: 30 % and 90 % | Live connect announcement: owner check (KI-029) |
| 2026-11-25 | DEV-01 | 26300 | same | Idle CPU with all system providers running (3 × 20 s, from outside, two runs) | 0.04–0.18 % | Machine load 15–18 %; privacy provider off: 0.08–0.22 % (no measurable cost) |
| 2026-11-25 | DEV-01 | 26300 | same | Phase 4 self-test | 44 pass / 11 fail / 2 inconclusive | Idle CPU with all providers 0.156 %; fails: startup 807 ms, 1–2 dropped frames per transition (KI-011/012). `docs/evidence/phase-4-selftest-2026-11-25.json` |
| 2026-11-25 | DEV-01 | 26300 | same | Phase 4B self-test with `--test-camera` | 62 pass / 13 fail / 2 inconclusive | Panel opens from idle, window 740×290 centred, shrinks back; camera off by default; mirror live in 0.8 s, released (exclusive open afterwards); panel-open CPU 0.82 %; fails: startup 967 ms, 1 dropped frame per transition (KI-011/012). `docs/evidence/phase-4b-selftest-2026-11-25.json` |
| 2026-11-25 | DEV-01 | 26300 | same | Built-in camera (camera mirror, SharedReadOnly) | Pass | Frames at ≤ 15 fps 320×240; nothing stored |
| | | | 2+ monitors, mixed DPI | Placement, display change, follow cursor | ⏳ | Owner hardware needed (KI-013) |
