# 0006 — Media sessions, waveform, and "hide while the app is in front"

- **Status:** accepted
- **Date:** 2026-11-25
- **Phase:** 3
- **Blueprint sections:** §6, §8.6.1, §8.6.2, §8.9

## Context

Phase 3 connects real media (Windows `GlobalSystemMediaTransportControlsSessionManager`, GSMTC) and a real
audio-level waveform. Facts measured on DEV-01 (Windows 11 build 26300, `docs/evidence/phase-3-*`):

- Media Player (`Microsoft.ZuneMusic_…!Microsoft.ZuneMusic`), Edge (`MSEdge`) and Chrome publish SMTC sessions with
  timelines and seek. Chrome reported a per-install id **`Chrome.<hash>`**, not `Chrome`, so a fixed id table failed.
- Chrome sent its own logo as "artwork" when the page's artwork could not load; Edge sent none.
- Chrome produced **0 change events in 5 s** while playing — event volume was not the CPU problem.
- With real media the Island used 3.4–9.3 % CPU. Turning off the 30 Hz waveform timer: 0.35 %. Turning off only the
  audio meter: 5.4 %. The cost was re-rendering the pill's images (app icon / artwork with high-quality scaling) on
  every waveform frame in the layered window.
- Owner requests (2026-11-25): hide an activity while its app is open in front, show it when minimised; no
  background colour behind the app icon.

## Decision

- **Structure:** provider contracts moved to `IrisAura.Island.Providers` (namespace `IrisAura.Island.Activities`) so
  real providers live there as §6 lays out; the engine stays in `IrisAura.Island`. `IActivityProvider.OnAction` returns
  whether the action was handled.
- **One media activity** for the chosen session (user's switcher choice → playing (current one if several) → current
  paused); other sessions travel with it for the expanded-view switcher, instead of competing for the bubble.
  A new session starting to play overrides an earlier switcher choice.
- **Resilience:** reconnect with 1/2/5/10/30 s back-off; fault after 8 consecutive failures. Control failures are not faults.
- **De-duplication:** a refresh whose only change is a timeline position matching the interpolated one (±1.5 s) is not
  republished; changes are debounced 150 ms. Artwork is cached per title|artist|app **only when present** (apps
  often publish the title first and the art later); the view reloads art when the bytes change for the same track.
- **App identity:** fixed table plus Chromium prefixes (`Chrome.`, `MSEdge.`, `Brave.`, `Opera.`, `Vivaldi.`); unknown
  unpackaged ids use the id as the process name; unknown packaged apps fall back to device-level audio.
- **Waveform:** NAudio (approved) CoreAudio peak of the app's own sessions matched by process name (browser audio runs
  in helper processes with the same name), else the default device; device and session list re-read every 2 s.
  Single level → 5 bars with per-bar modulation, 40 ms attack / 180 ms release, flat line when paused. Timers run
  only while visible.
- **Rendering cost:** artwork and app icon are cached with `CacheMode="BitmapCache"` so they are rasterised once;
  measured afterwards 1.45–1.52 % CPU with real Chrome media and a live waveform (target 1.5 %).
- **Hide while in front (owner rule):** a persistent activity is not shown while the foreground window belongs to its
  source app (process name, or AUMID for packaged apps); it reappears when the app is minimised **or another app comes
  to the front**. The Island's own windows (menus, debug panel) do not count. Transients still show. Default on
  (`IslandController.HideWhenSourceAppInFront`); a switch in the settings window (§8.7) later.
- **Open the app:** packaged apps via `IApplicationActivationManager`; unpackaged via restoring the main window and
  `SetForegroundWindow` in response to the user's click. No `AttachThreadInput`, no input injection.
- **Icon look:** app icon directly on the black pill (no background colour); artwork replaces the icon when present.
- **Privacy:** titles, artists and artwork go only to the Island's memory; never logged or persisted. `--media-probe`
  records presence flags only; `--save-artwork` exists for test tones and is never on by default.

## Consequences

- "In front" (not "minimised only"): a browser playing music behind another window still shows on the Island. If the
  owner wants strictly "only when minimised", it is a one-line rule change.
- Browsers are one app: with the browser in front, its media hides whichever tab plays.
- Old-style UWP apps hosted in `ApplicationFrameHost` are not matched by the in-front rule (not yet observed with media apps).
- Working set with real media is 120–140 MB (target 120 MB) — KNOWN_ISSUES KI-022.

## Alternatives considered

| Option | Why not |
|---|---|
| One activity per session | Second media session takes the bubble; §8.6.1 says others go to the queue/switcher |
| Undocumented NPSM | Forbidden (BLUEPRINT §8.6.1, CLAUDE.md) |
| WASAPI loopback + FFT (option B) | Whole-system audio, more CPU; kept as fallback level only |
| Lower waveform rate (20 Hz) | Not needed once images were cached; §8.6.2 asks for 30 Hz |
| "Only when minimised" rule | Browser media would rarely show; offered to owner as a switch |
