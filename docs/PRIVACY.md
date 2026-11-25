# Privacy

How IrisAura handles personal data (BLUEPRINT §9.3). **This is an engineering document, not legal
advice.** The deploying organisation (usually the client, as data fiduciary / controller) must get
its own legal review.

## Principles

- **Local only.** No cloud processing. No network calls unless a feature explicitly says so (only the
  updater, from Phase 9, which sends no personal data).
- **No telemetry by default.** Any future telemetry is opt-in and never contains biometric data or
  notification content.
- **Minimum data, shortest time.**

## IrisAura Island

| Data | Source | Stored? | Notes |
|---|---|---|---|
| Media title/artist/artwork | Windows media sessions | In memory; artwork cache in memory | Not persisted |
| Notification title/text | Windows notification listener (user consent) | **In memory only**, cleared on dismiss | Not implemented yet (KI-026); "Hide content" option planned |
| Mic/camera in-use app names | Windows consent store (read-only) and CoreAudio capture sessions (process names) | In memory | Shown as dots and in the menu; never logged. Best-effort (KI-028) |
| Bluetooth device names and battery | Windows device APIs | In memory, only while the 3 s banner shows | Names are the user's own device names; never logged |
| Download file names and sizes | File-system events in the Downloads folder (names and sizes only; file contents never read) | In memory while the download shows | Never logged |
| Volume, brightness, battery level | Windows | Not stored | — |
| Timer | User | `%LocalAppData%\IrisAura\island-timer.json`: duration and end time only | Deleted when the timer ends or is cancelled |
| Settings | User | `%LocalAppData%\IrisAura\island-settings.json`: provider id → on/off | — |
| Quiet mode, hide-until | User | `%LocalAppData%\IrisAura\island-state.json` | — |
| File tray paths | User (files dropped on the Island) | `%LocalAppData%\IrisAura\island-shelf.json`: paths only, max 30 | Files are never copied, opened (except by the user's click) or read; paths never logged; "Clear tray" deletes the list |
| Camera mirror frames | Webcam, **only after the user switches the mirror on**, only while the panel shows | **Never stored**: drawn and dropped; the last frame is cleared when the camera stops | Shared read-only camera access; camera dot on while it runs; setting `CameraMirror` in island-settings.json |
| Calendar | — | — | Not read: the strip shows dates only (events need consent and package identity, KI-035) |
| Logs | Island | `%LocalAppData%\IrisAura\logs` | Provider ids, states, error types. Never notification content, media titles, device, app or file names |

Call detection (beta, off by default) uses only which known call app holds the microphone and whether it
plays audio; it never records or analyses audio. The `--system-probe --probe-mic` diagnostic opens the
microphone for a few seconds with a visible notice and discards the audio unread.

## IrisAura Face Lab (Phase 6+)

- Explicit, affirmative **consent screen** before any capture: purpose, data types, local processing,
  retention, deletion, limitations, and a no-biometric alternative (BLUEPRINT §7.6).
- **No raw face images stored** by default. Templates/embeddings are special-category biometric data:
  encrypted with a device-bound key, per-user access control, never logged.
- **Never collects or stores a Windows password.**
- One-action **"Erase all face data"**, covering primary storage, caches, temp files and diagnostics.
- Evaluation datasets (Phase 6–7) require separate written consent, are access-controlled and are
  never committed to the repository (`tests/datasets/` is git-ignored).

## Legal frameworks to review before deployment

- **India DPDP Act 2023** — Rules notified 13 Nov 2025; core obligations apply from **13 May 2027**.
  Client is likely the Data Fiduciary when deploying to employees.
- **GDPR** — face templates are Art. 9 special-category data: explicit consent and a DPIA for EU
  deployments.
- Employment-monitoring and biometric laws in every deployment country.

Templates to produce before any pilot: consent notice, retention policy, deletion procedure.
