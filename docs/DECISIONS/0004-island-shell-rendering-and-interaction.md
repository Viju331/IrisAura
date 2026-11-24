# 0004 — Island shell: rendering, click-through and interaction

- **Status:** accepted
- **Date:** 2026-11-24
- **Phase:** 1
- **Blueprint sections:** §8.2, §8.3, §8.4, §8.8

## Context

Phase 1 built the Island window. Several choices were settled by measurement with the built-in
self-test (`IrisAura.Island.exe --selftest`, evidence in `docs/evidence/`):

1. **Drop shadow cost.** With a WPF `DropShadowEffect` on the pill, the fake 30 Hz waveform cost
   4.8–9.2 % CPU, startup was 3.0–3.3 s, and transitions had frames up to 383 ms. Without the effect:
   ~1.2 % CPU, ~1.5 s startup. In a layered (`AllowsTransparency`) window every update re-rasterises the
   blur and copies the whole surface.
2. **Click-through.** The self-test confirmed on Windows 11 build 26300:
   - pixels with alpha 0 pass clicks to the window below even without `WS_EX_TRANSPARENT`;
   - semi-transparent shadow pixels do catch clicks, so the `WS_EX_TRANSPARENT` toggle is needed;
   - with the toggle set, the whole window is click-through.
3. **Window size.** A generic expanded activity (200 DIP tall) plus spring overshoot did not fit the
   448 × 210 DIP window first planned (unit test `Every_state_fits_the_fixed_window_with_overshoot_room`).
4. **Interop.** Only ~25 Win32 functions are needed.
5. **Idle pill.** At top-centre it sits over title bars and browser tabs, and has no action of its own.
6. **CPU measurement method.** The first 10 s after showing media includes .NET warm-up (JIT/tiered
   compilation) and read 4–6 % CPU; steady state read ~1.1–1.4 %. The dev machine also ran at 14–100 %
   load from other apps, which moved single measurements by several times.

## Decision

- **Shadow:** `PillShadow`, a few concentric translucent rounded rectangles drawn in `OnRender`, replaces
  `DropShadowEffect` for the pill and the bubble (BLUEPRINT §8.3 allows "a pre-rendered … for performance").
- **Click-through:** layered-window alpha + `WS_EX_TRANSPARENT` toggled by a 30 Hz cursor poll, as in
  BLUEPRINT §8.3. `HTTRANSPARENT` is not used. The **idle** pill stays click-through (it has no action),
  so it never blocks clicks on title bars and tabs; hover still works because hover is polled.
- **Window:** fixed 480 × 250 DIP (largest pill + ~8 % overshoot + shadow), created once; only the pill animates.
  New content is laid out *before* the animation clock starts, so template instantiation does not land in
  an animation frame.
- **Interop:** hand-written `[LibraryImport]` signatures (`Interop/NativeMethods.cs`), no CsWin32 package.
  Requires `AllowUnsafeBlocks` in `IrisAura.Island`. Only documented APIs; the WinEvent callback catches
  every exception so none crosses the native boundary.
- **Keyboard focus mode:** the only time the Island takes the foreground, triggered by the user's
  `Ctrl+Alt+Shift+I` (`WM_HOTKEY`); Esc or deactivation restores `WS_EX_NOACTIVATE` and the previous window.
- **Full-screen:** shell state (`SHQueryUserNotificationState`) plus "foreground window on the Island's
  monitor covers that monitor", excluding the desktop/shell, Island windows and maximised windows with a
  title bar (which can cover the monitor when the taskbar auto-hides).
- **Measurement method (self-test):** CPU is the median of 3 × 10 s samples after a 5 s warm-up, reported
  with whole-machine load; frame intervals come from `CompositionTarget.Rendering` and a frame > 25 ms
  (1.5 × the 60 fps budget) counts as dropped.
- **Startup:** ReadyToRun was tried (scratch publish): best 775 ms vs 847 ms for JIT, both dominated by
  machine load and disk cache. ReadyToRun is deferred to Phase 9 packaging (it needs `RuntimeIdentifiers`
  and per-RID lock files).
- **Single instance & control:** `Local\IrisAura.Island.Instance` mutex; `Local\` named events let a
  second launch show the debug panel (`--debug`) or quit (`--quit`).

## Consequences

- The shadow is a little harder-edged than a Gaussian blur; acceptable on the black pill.
- A fast flick from the pill to just outside it and a click within ~33 ms could land on the window's
  shadow area; harmless (nothing happens) and consistent with BLUEPRINT §8.3.
- Any process running as the same user in the same session can signal the quit/show-debug events
  (same trust level as the user; THREAT_MODEL T-15).
- Performance acceptance numbers must be re-measured on an otherwise idle machine; dev-machine
  measurements are recorded with their load.

## Alternatives considered

| Option | Why not |
|---|---|
| Keep `DropShadowEffect` | 4–8× CPU on every waveform update; slower startup |
| No shadow | Pill edges vanish on dark backgrounds; the vector shadow costs about the same as none |
| `WM_NCHITTEST` → `HTTRANSPARENT` | Only forwards to windows of the same thread (BLUEPRINT §8.3) |
| Shrink the window when at rest | Less surface to copy, but resizing a layered window at the start of an animation shows a one-frame jump; measured CPU did not depend clearly on window area |
| Non-layered DWM-extended-frame window | Unproven with WPF + `WS_EX_TRANSPARENT`; revisit only if Phase 3 real waveform misses the budget |
| CsWin32 | Approved, but adds a generator and API-shape uncertainty for ~25 functions |
