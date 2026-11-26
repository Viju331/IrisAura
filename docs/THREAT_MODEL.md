# Threat Model

Living document. Starts from BLUEPRINT §9.1 and is refined per phase. Each threat lists where it is
mitigated and how the mitigation is tested. Status: ⏳ planned · 🔨 implemented · ✅ tested.

## Assets

| Asset | Where | Sensitivity |
|---|---|---|
| Face templates / embeddings | Face Lab store (Phase 6) | Special-category biometric data; never logged |
| Windows credentials | Not stored by default (BLUEPRINT §7.8) | Critical |
| Notification content | Island in-memory queue only | Personal data; never persisted or sent |
| Island SDK channel | Per-user named pipe | Integrity of what the Island shows |
| Signing keys | HSM / hardware token (never in repo) | Critical |
| Users' ability to sign in to Windows | Built-in providers | Must never be degraded |

## Trust boundaries

1. Interactive user session ↔ other local processes (Island SDK pipe).
2. User session ↔ LocalSystem services (Face pipes; only if Phase 0B/0C pass).
3. LogonUI / secure desktop ↔ credential provider DLL (conditional track).
4. Build/supply chain ↔ third-party packages and models.

## Threats

| ID | Threat | Boundary | Mitigation | Test | Status |
|---|---|---|---|---|---|
| T-01 | Rogue app spams or spoofs Island activities | 1 | Per-user pipe DACL (user only, NETWORK denied); identity = OS-read program path + app name; ids namespaced per identity; 10 updates/s per activity; 5 activities per app; priority 10–60; text sanitised; icons ≤ 128 × 128 checked before decoding; 64 KB lines; version check; 20-error disconnect; user can block an app (ADR 0009) | `SdkTests` (untrusted app, flood, rate limit, oversized, malformed, blocking) | ✅ |
| T-02 | Pipe squatting: malicious process creates the SDK pipe first | 1 | Island creates the pipe with `FILE_FLAG_FIRST_PIPE_INSTANCE` and reports itself unavailable if the name is taken; the client verifies the server is `IrisAura.Island.exe` in its session with an intact signature from a known publisher or pinned thumbprint before sending anything | `SdkTests` (squatted name, fake server refused); `scripts/sdk-e2e.ps1` (dev-signed accepted only when pinned, unsigned refused) | ✅ |
| T-03 | Pipe-name injection via crafted SID | 1 | `PipeNames.IslandActivities` accepts only digits/dashes in SID form | `PipeNamesTests` | ✅ |
| T-04 | Notification content leaks | 1 | "Hide content" option; no persistence; no network | Notification listener deferred (KI-026, T-24) | ⏳ |
| T-05 | Island depends on Face code (scope creep into Release 1) | — | Architecture rule | `ArchitectureTests` | ✅ |
| T-06 | Photo/screen replay (RGB) | 2 | Honest tiers; liveness research only; never an auth factor | Phase 7 evaluation | ⏳ |
| T-07 | Virtual/injected camera | 2 | Camera pinning (ID + VID/PID); reject virtual cameras | Phase 0B/6 | ⏳ |
| T-08 | Rogue process impersonates broker or provider | 2, 3 | Separate pipes, strict DACLs, token/SID/session validation; PID/path checks supplementary | Phase 8 | ⏳ |
| T-09 | User locked out by provider bug | 3 | Never filter providers; disabled by default; tested out-of-band recovery | Phase 0C recovery drill | ⏳ |
| T-10 | Credential theft | 2, 3 | No password vault by default; any lab vault needs separate threat model + review | Phase 8 gate | ⏳ |
| T-11 | Malicious or substituted NuGet package | 4 | nuget.org only + source mapping; pinned versions; lock files; `-Locked` CI restore | `build.ps1 -Locked` | 🔨 |
| T-12 | Model file tampering / licence violation | 4 | SHA-256 in `models/SHA256SUMS`, licences in `models/LICENSES/`, CI hash check | Phase 6 | ⏳ |
| T-13 | DLL hijack / tampering of installed files | — | Install to `%ProgramFiles%` (admin-only), self-contained app (no DLL search into user folders for the runtime), Authenticode-signed binaries; `SetDefaultDllDirectories` still to add | Release 1 install test (location, signatures) | 🔨 |
| T-14 | AV/EDR quarantine | — | Signing, no packers, no input injection | Phase 9 | ⏳ |
| T-17 | A buggy or hostile provider floods updates, takes another provider's activity id, or crashes the Island | 1 | Per-provider ownership (`IdConflict`), stale-sequence rejection, coalescing, exceptions isolated to the provider (faulted, activities removed); SDK rate limits in Phase 5 | `ActivityManagerTests`, `ProviderHostTests`, self-test | ✅ |
| T-18 | Notification or activity content leaks to disk | 1 | Only quiet mode and hide-until are persisted; logs carry provider ids and error types/messages only | `IslandStateStoreTests` (only two keys); review | ✅ |
| T-19 | Media metadata or artwork leaks (titles reveal what the user listens to) | 1 | Kept in memory only; never logged or persisted; `--media-probe` records presence flags only; artwork saving only with explicit `--save-artwork` | Probe evidence contains no title strings (checked) | ✅ |
| T-20 | Reading other processes' identity for the "in front" rule and audio matching | 1 | Only process name and (packaged) AUMID via `PROCESS_QUERY_LIMITED_INFORMATION`; no window titles or contents | Review | ✅ |
| T-21 | Device, app and file names (Bluetooth names, apps using the mic/camera, download names) leak to logs or disk | 1 | Shown only; held in memory while displayed; logs carry provider ids and error types; probe report holds counts/flags only | Review; `docs/evidence/phase-4-system-probe-*.json` contains no names (checked) | ✅ |
| T-22 | A local process fakes mic/camera use (writes the user's consent store) or hides its own use | 1 | Accepted: same-user registry is writable by the user's processes; indicators are best-effort and never a security control. Microphone also from audio sessions | Review | ✅ accepted |
| T-23 | Island diagnostics change system state (volume, brightness, microphone) | 1 | Only with explicit `--system-probe` flags; ±1 % and restored; microphone with visible notice, audio discarded | Probe evidence (`restored: true`) | ✅ |
| T-24 | Notification listener access (package identity, dev certificate trust) weakens the machine trust store | 1 | Not implemented; requires owner approval; dev certificate only in CurrentUser TrustedPeople on test machines, never shipped (BLUEPRINT §8.6.9) | Phase 4 follow-up | ⏳ |
| T-25 | Camera mirror leaks images (stored, logged, left running, or takes the camera from another app) | 1 | Opt-in; SharedReadOnly; runs only while the panel shows; disposed on close; frames dropped; last frame cleared; camera dot | Self-test: off by default, live, released, exclusive open afterwards | ✅ |
| T-26 | File tray list reveals file names/paths | 1 | Local `island-shelf.json` in the user's profile only; never logged or sent; one-click clear | `PanelTests` (persist/clear) | ✅ |
| T-27 | An SDK activity's link runs code or opens local files when clicked | 1 | `openUri` scheme policy (http/https/app schemes; file, script, search, troubleshooter, Office-command and other system schemes refused); opened only on the user's click via the shell | `SdkTests` (link policy); e2e `badLinkRefused` | ✅ |
| T-28 | A hostile same-user process exhausts SDK connections, uses an app name of its choice from its own program, or replaces a user-writable `IrisAura.Island.exe` | 1 | Accepted at same-user trust: 16-connection cap, per-identity limits, blocking; installing to `%ProgramFiles%` and production signing close the replacement path (Phase 9) | Review | ✅ accepted |
| T-29 | A tampered or downgraded update is installed | — | Signed manifest (ECDSA P-256, non-exportable key), product and no-downgrade check, size + SHA-256, Authenticode signer pinned/known; dev key only in dev builds | `UpdateVerifierTests`; install test (tampered MSI, edited manifest rejected) | ✅ |
| T-15 | Another same-user process signals the Island's `Local\` quit / show / show-debug events | 1 | Accepted: same trust level as the user; worst case the Island quits or shows its debug panel. Revisit if the debug panel ever exposes sensitive data | Phase 1 review | ✅ accepted |
| T-16 | Island steals focus while the user types (credentials typed into the wrong window) | 1 | `WS_EX_NOACTIVATE`, `MA_NOACTIVATE`, `ShowActivated=False`; foreground only via the user's own hotkey | Self-test "no focus taken" (9/9) | ✅ automated, ⏳ manual |
