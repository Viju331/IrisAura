# 0007 — System providers: sources, availability and what was deferred

- **Status:** accepted
- **Date:** 2026-11-25
- **Phase:** 4
- **Blueprint sections:** §8.5, §8.6.3–§8.6.11, §8.7, §10 Phase 4

## Context

Phase 4 adds volume, brightness, power, Bluetooth, privacy indicators, timers and downloads as independent
providers, keeps call detection a labelled heuristic, and allows notifications only once package identity,
permission UX, privacy handling and build compatibility are proven. Testing on DEV-01 (Windows 11 26H2,
build 26300) produced evidence that changed two blueprint assumptions:

- The capability consent store (`HKCU\…\CapabilityAccessManager\ConsentStore`) has not been written since
  2026-10-12 for **any** app on this build. Opening the microphone from IrisAura did not create an entry
  (`docs/evidence/phase-4-mic-probe-2026-11-25.json`: `consentStoreRecordedIt: false`, 202 capture packets).
- The first `PowerManager` call blocks for ~0.5 s (log: power provider start 03.473 → 04.030).

## Decision

1. **Microphone in use** comes from two sources merged: the consent store (watched with
   `RegNotifyChangeKeyValue`, zero idle cost) **and** CoreAudio capture sessions in `AudioSessionStateActive`
   (documented `IAudioSessionManager2`; event-driven through session-created and per-session state/disconnect
   notifications, device list refreshed every 30 s). Measured: detected 0.37–0.8 s after the microphone opens,
   0.13–0.26 s after it closes. A first version read the sessions once a second and cost 0.15–0.2 % idle CPU. **Camera in use** has no
   documented per-process API, so it stays consent-store only and is labelled best-effort (KI-028).
2. **Indicator colours** use brand tokens: microphone `Aura.Violet`, camera `Aura.Cyan`, drawn as 7 px dots just past
   the Island's trailing edge (pill or bubble), so they never cover activity content and show on the idle pill.
   The brand palette has no orange/green, and copying another platform's dot colours is avoided (CLAUDE.md trade dress).
   The app list is shown in the right-click/tray menu; the expanded "which apps" view comes with Phase 4B.
3. **Tone** (`ActivityTone`: Default / Success / Alert) is added to `CompactContent` so generic banners colour their
   glyph with brand tokens only (Success = `Aura.Cyan`, Alert = `Aura.Error`).
4. **Unavailable is not failed.** `IActivitySink.ReportUnavailable(reason)` stops a provider quietly when the device
   lacks the feature (no battery, no WMI brightness, no Bluetooth adapter, no Downloads folder). The Activities menu
   shows "(not available on this device)"; choosing it again retries.
5. **Every provider can be switched off** from the Activities submenu (tray and right-click). Choices persist in
   `%LocalAppData%\IrisAura\island-settings.json` (provider id → on/off only). Call detection is off by default.
6. **Call detection (beta)** starts only when a known call app holds the microphone **and** has an active audio output
   session; it ends 3 s after the microphone is released. Browser calls (Meet/Teams/Zoom in a tab) are not detected:
   that needs window titles, which the Island does not read (THREAT_MODEL T-20).
7. **Timers** persist durations and times only (`island-timer.json`), wake the provider twice (10 s mark, end) instead of
   ticking, and play `SystemSounds.Asterisk` with a shake when they end. A timer that ended while the Island was closed
   shows the banner once without sound. `Ctrl+Alt+Shift+T` starts 5 min or pauses/resumes.
8. **Downloads** show bytes written (no percentage; the total is unknown) and no animated indeterminate bar (it would
   render continuously and break the idle CPU budget). Click opens the Downloads folder.
9. **Deferred, by design:**
   - **Notifications (§8.6.9):** need a signed sparse package with a development certificate trusted on the machine and
     package registration. That changes the machine's trust store, a security boundary, so it waits for owner approval
     (see PROGRESS Phase 4).
   - **Hiding Windows' volume flyout (§8.6.3):** no supported API; would mean manipulating a shell-internal window.
10. Slow WinRT/WMI initialisation runs off the UI thread (power, brightness, Bluetooth).

## Consequences

- Idle CPU with all providers running measured 0.04–0.18 % (three 20 s samples, two runs, machine load 15–18 %);
  with the privacy provider off 0.08–0.22 %: no measurable cost left.
- The microphone indicator works on builds where the consent store is stale; the camera indicator may not (KI-028).
- The contract gained `ReportUnavailable` and `CompactContent.Tone`: SDK messages (Phase 5) map onto them.
- Bluetooth battery uses the device property `{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2` from any node in the device's
  container; it was present for two paired devices on DEV-01, but live connect announcements need an owner test.

## Alternatives considered

| Option | Why not |
|---|---|
| Consent store only (blueprint) | Not updated on build 26300; indicator would never show |
| Poll the consent store every 1 s | Same data problem; the change notification costs nothing when idle |
| Undocumented CapabilityAccessManager COM interfaces | CLAUDE.md: no undocumented APIs |
| Orange/green dots | Not brand tokens; mirrors another platform's trade dress |
| Reading capture sessions once a second (blueprint's "poll every 1 s") | Measured 0.15–0.2 % idle CPU, which breaks the 0.3 % budget with everything else |
| Ticking the timer provider every second | Unneeded wake-ups; the view counts down on its own while visible |
