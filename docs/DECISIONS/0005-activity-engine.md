# 0005 — Activity engine: ownership, arbitration, isolation, menus

- **Status:** accepted
- **Date:** 2026-11-24
- **Phase:** 2
- **Blueprint sections:** §8.5, §8.6.14, §8.9, §10 Phase 2

## Context

BLUEPRINT §8.5 gives the priority table and five arbiter rules but leaves several behaviours open:
how ties break, who may update an activity, what a duplicate id means, how a user's swap interacts with
new activities, how providers fail, and how the user reaches the menu when the idle pill is click-through
(ADR 0004).

## Decision

**Ownership and lifecycle (`ActivityManager`)**
- Every activity belongs to the provider that started it. Another provider cannot update or end it
  (`IdConflict`). Starting an existing id from the same provider replaces its content and keeps `StartedAt`.
- Updates are partial. A provider may send a monotonic `Sequence`; a non-newer sequence is `Stale`.
  Updates to unknown/ended/ending activities are ignored, so late events never resurrect an activity.
- Transients expire `Ttl` after their **last update** (a HUD stays while the user keeps pressing keys);
  persistent activities expire `Ttl` after start unless an update sets a new `Ttl`.
- `End(finalText, dismissAfter)` shows a final message, then removes the activity (SDK `end`).
- Custom/SDK priorities are clamped to 10–60, others to 0–100. Ids: 1–128 characters, no control characters.
- `Navigation` (in the §8.5 enum but not the table) defaults to 55, between Timer and Media.

**Arbitration (`PriorityArbiter`, pure)**
- Persistent ties: earlier `StartedAt` wins, then ordinal id — layouts never flap when an activity updates.
- Transient ties: newest update wins (the latest notification is shown first); others wait.
- Paused media keeps its place for 30 s, then joins the queue; the arbiter reports `NextChangeAt` so the
  controller re-arranges exactly when time alone changes the layout.
- Swap (bubble click) and cycle (mouse wheel) set a `UserPreference` that is cleared when either activity
  ends **or when a more important persistent activity starts** (an incoming call should not stay hidden behind
  the user's earlier choice).
- Quiet mode = user quiet mode OR Windows quiet time (`QUNS_QUIET_TIME`); it suppresses notification banners only.
- Privacy indicators never take the pill; the layout lists them for the dot indicator (Phase 4).

**Isolation (`ActivityProviderHost`)**
- Providers talk to an `IActivitySink` usable from any thread. Operations are queued per provider and
  drained on the UI context in one post per burst; an update directly following an update to the same
  activity is merged (order of start/update/end is preserved).
- Any exception from `Start`, `OnAction`, or a `ReportFailure` call faults that provider only: its sink is
  closed, `Stop` is called (errors swallowed), its activities are removed, the error type and message are
  logged. It can be restarted.

**Menus and tray**
- A notification-area icon (documented `Shell_NotifyIcon`, NOTIFYICON_VERSION_4) gives access to the menu when
  the pill is idle or hidden; the icon is the tray glyph rendered at the system small-icon size, light or dark to
  match the taskbar, re-added on `TaskbarCreated`.
- Menus are native (`TrackPopupMenuEx`). Windows requires the owner window to be foreground while a menu is
  open; IrisAura takes the foreground only for the menu's lifetime (user-initiated) and hands it back to the
  previous window afterwards.
- Menu: Quiet mode, Hide for 1 hour / Show, Start timer (1/5/10/25 min), Settings (disabled until §8.7),
  Debug panel (debug only), Quit. `Ctrl+Alt+Shift+I` or a second launch shows a hidden Island.

**Persistence**: only `QuietMode` and `HiddenUntil`, in `%LocalAppData%\IrisAura\island-state.json`, written
atomically. No activity or notification content is persisted.

**Logging**: Serilog to `%LocalAppData%\IrisAura\logs\island-*.log` (7 daily files, 5 MB cap). Provider ids,
error types and messages only — providers must not put user content in exception messages.

## Consequences

- SDK clients (Phase 5) get conflict/stale protection and coalescing for free.
- A provider author must keep exception messages free of personal data (review checklist item).
- The menu briefly takes the foreground; tested to return focus to the previous window.

## Alternatives considered

| Option | Why not |
|---|---|
| WPF `ContextMenu` on the pill | Unproven focus behaviour on a no-activate window; unavailable when the pill is idle/hidden |
| Ties by most recent | Flapping when two equal activities alternate updates |
| Keep user preference until it ends | A new call or face event would stay behind the user's earlier choice |
| Restart faulted providers automatically | Could loop on a persistent fault; provider-specific back-off arrives with real providers (e.g. GSMTC, Phase 3) |
