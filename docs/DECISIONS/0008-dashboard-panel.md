# 0008 — Dashboard panel: window sizing, idle hover, camera mirror and file tray

- **Status:** accepted
- **Date:** 2026-11-25
- **Phase:** 4B
- **Blueprint sections:** §8.3, §8.6.6, §8.6.12, §8.8, §8.13A

## Context

The owner asked for a dashboard-style expanded panel (§8.13A): floating pill kept; tabs Island / Tray; a large media
card, a calendar strip, an opt-in camera mirror and a file tray; Bluetooth devices in the new design. Constraints: the
§8.8 budgets (idle < 0.3 %, media + waveform < 1.5 %, 60 fps), the shell rules of §8.3 (never take focus,
click-through, full screen), the camera only with consent and released when the panel closes, and no calendar access
without consent.

Measurements that shaped the design (DEV-01, `docs/evidence/phase-4b-selftest-2026-11-25.json`):

- Every redraw of a layered window copies its whole surface (ADR 0004/0006). The panel needs a 740 × 290 window
  (1.8 × the 480 × 250 compact window).
- First panel run: 4.4 % CPU with the panel open on the media card. The fake media view re-laid out its timeline text
  at 30 Hz (the real view does it at 4 Hz) and the waveform ran at 30 Hz on the larger surface.
- Closing the panel dropped 7 frames before the outgoing content was cached.

## Decision

1. **The window grows only for the panel.** It is resized once, around the same top-centre point, just before the
   panel opens, and shrunk back once after the close animation ends; never per frame. Compact states keep the small
   window, so compact CPU is unchanged.
2. **Inside the panel the waveform runs at 15 Hz** (`IPanelAwareView`); smoothing is time-based, so it only gets
   coarser. The self-test's fake media now updates its timeline at 4 Hz like the real view. Result: **0.82 %** with
   the panel open.
3. **Outgoing content is bitmap-cached while it fades** (its timers have stopped): closing dropped 7 → 1 frame.
4. **Hovering the idle pill opens the panel** (as in the design the owner chose). The idle pill therefore takes clicks
   *while the cursor is on it*; everywhere else it stays click-through. Also reachable from the tray menu
   ("Open panel") and by dragging files onto the Island.
5. **The panel replaces the old expanded views.** The main activity's expanded view sits in the panel's "now" card
   (media card, timer, call, download…); its compact and banner views are unchanged.
6. **Camera mirror:** off by default; the user switches it on in the panel (persisted as `CameraMirror` in
   `island-settings.json`). It runs only while the panel's Island tab shows, opens the camera with
   `MediaCaptureSharingMode.SharedReadOnly` (never takes the camera away from a call), 320 × 240 at most 15 fps,
   frames drawn and dropped (the last frame is cleared on stop), camera dot on while it runs. `MediaCapture` is
   disposed when the panel closes or the Tray tab is chosen; the self-test proves the release by opening the camera
   with exclusive control afterwards.
7. **Calendar strip shows dates only.** Events need `Windows.ApplicationModel.Appointments` with package identity and
   the user's consent (the same sparse-package step as notifications, KI-026); no calendar data is read.
8. **File tray** stores paths only (`island-shelf.json`, newest first, max 30, missing files dropped on load); files are
   never copied, read or logged. Drop on the Island (any state) → panel opens on the Tray tab; click opens the item;
   drag out starts a file drag; remove one or clear all.
9. **Bluetooth "connected"** uses its own banner: the device glyph inside a ring that fills to the battery level
   (faint full ring when unknown), with a short scale-in; no third-party artwork.

## Consequences

- Budgets met in the final self-test: idle 0.117 % (providers stopped) and 0.117 % (all providers running), media +
  waveform 1.055 %, panel open 0.82 %; transitions keep the known 1-frame drops (KI-011).
- Snapshots of the panel are rendered from the Island's own visual (`--snapshots`), never from the screen.
- Drag-and-drop from an elevated app (e.g. Explorer run as administrator) into the non-elevated Island is blocked by
  Windows (UIPI); not worked around.

## Alternatives considered

| Option | Why not |
|---|---|
| Create the window at panel size from the start | Every compact waveform frame would copy 1.8 × the pixels (budget) |
| Keep 30 Hz waveform in the panel | Over budget on the larger surface |
| Separate non-layered window for the panel | Two windows to keep in sync for morphs, focus and click-through; revisit if budgets tighten |
| Camera in exclusive mode | Would take the camera from Teams/Zoom during a call |
| Copy dropped files into an IrisAura folder | Blueprint: shortcuts, not copies; avoids duplicating user data |
