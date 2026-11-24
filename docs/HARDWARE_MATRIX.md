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
| | | | 2+ monitors, mixed DPI | Placement, display change, follow cursor | ⏳ | Owner hardware needed (KI-013) |
