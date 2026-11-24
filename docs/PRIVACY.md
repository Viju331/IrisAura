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
| Notification title/text | Windows notification listener (user consent) | **In memory only**, cleared on dismiss | "Hide content" option shows the app name only |
| Mic/camera in-use app names | Windows privacy (ConsentStore) | In memory | Best-effort |
| Timers, file-shelf paths, settings | User | `%LocalAppData%\IrisAura\` | No content of shelved files is copied |
| Logs | Island | `%LocalAppData%\IrisAura\logs` | Never notification content, media titles or file names |

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
