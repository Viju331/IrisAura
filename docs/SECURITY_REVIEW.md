# Security Review

Record of security reviews and gate sign-offs (BLUEPRINT §7.8, §9, §10). A gate passes only with a
named reviewer, a date and linked evidence. Independent review means someone other than the author.

## Gates

| Gate | Phase | Required before | Reviewer | Date | Result | Evidence |
|---|---|---|---|---|---|---|
| Pre-logon camera feasibility (A/B/C outcome) | 0B | Phase 8 | | | ⏳ | |
| Credential Provider feasibility + recovery | 0C | Phase 8 | | | ⏳ | |
| Account-type support matrix + credential lifecycle design | 0C | Phase 8 | | | ⏳ | |
| Island SDK pipe security (ACL, squatting, impersonation, rate limits) | 5 | Release 1 | self-review only; **independent reviewer needed** | 2026-11-25 | ⏳ self-review passed | ADR 0009, `SdkTests`, `docs/evidence/phase-5-sdk-e2e-2026-11-25.json` |
| Presentation-attack evaluation methodology | 7 | Phase 8 | | | ⏳ | |
| Independent security review of sign-in prototype | 8 | Any external pilot | | | ⏳ | |
| Legal / privacy review | 8, 9 | Any external pilot / release | | | ⏳ | |
| Release security checklist (signing, SBOM, update verification) | 9 | Each release | self-review (Release 1, development-signed) | 2026-11-26 | ⏳ partial | ADR 0010; `docs/evidence/release-1-install-test-2026-11-26.json`; production signing, timestamp and SBOM outstanding |

## Phase checklists

### Phase 0A (repository) — self-review, 2026-11-24

- [x] No secrets, certificates or private keys in the repository (`.gitignore` blocks `*.pfx`, `*.p12`, `*.snk`, `*.cer`).
- [x] NuGet restores only from nuget.org (`nuget.config` clears machine feeds; source mapping).
- [x] All package versions pinned centrally; lock files enabled; `-Locked` build fails on drift.
- [x] Credential-provider project is inert: no COM classes, no `DllRegisterServer`, cannot be registered.
- [x] No network, telemetry or logging code added.
- [x] Biometric datasets path (`tests/datasets/`) ignored by git.
- [x] Native hardening verified in the x64 Release build (2026-11-24, `dumpbin`): Control Flow Guard, CET compatible, high-entropy ASLR, NX; static CRT (only `KERNEL32.dll` imported); /W4 /WX clean; exports limited to `DllCanUnloadNow`/`DllGetClassObject`. ARM64 pending KI-010.

### Phase 1 (Island shell) — self-review, 2026-11-24

- [x] No input injection or keystroke simulation. `SetForegroundWindow` is used only (a) in keyboard focus mode after the user's own hotkey and (b) to hand focus back to the window the user came from; the self-test also uses it on its own probe windows.
- [x] Only documented Win32 APIs (`SetWinEventHook` out-of-context, `RegisterHotKey`, `SHQueryUserNotificationState`, `GetMonitorInfo`, `GetDpiForMonitor`, window styles). No undocumented APIs, no hooks into other processes.
- [x] The WinEvent native callback catches every exception (none crosses the native boundary).
- [x] No network, telemetry, file writes or logging of content. The only file written is the self-test report, to a path the user passes.
- [x] Fake activities read no system data (no media sessions, notifications or devices yet).
- [x] Island projects still reference no Face project (architecture tests).
- [x] Same-session named events (`Local\IrisAura.Island.Quit`, `…ShowDebug`) accepted as low risk: only processes of the same user/session can signal them (THREAT_MODEL T-15).

### Phase 2 (activity engine) — self-review, 2026-11-24

- [x] Providers are isolated: exceptions from start, actions or reported failures disable only that provider (tests + self-test).
- [x] Activity ids are owned by their provider; other providers cannot update or end them.
- [x] Persisted state holds only `QuietMode` and `HiddenUntil` (test asserts the only keys).
- [x] Logs: provider ids, error types and messages, startup time. No activity or notification content is logged. Checklist for future providers: exception messages must not include user content.
- [x] Tray icon and menus use documented APIs (`Shell_NotifyIcon`, `TrackPopupMenuEx`); the menu takes the foreground only while open, after a user click, and returns it.
- [x] Tray icon colour reads `HKCU\…\Themes\Personalize\SystemUsesLightTheme` (read-only).
- [x] New package Serilog / Serilog.Sinks.File: approved in BLUEPRINT §4, pinned, Apache-2.0, recorded in THIRD_PARTY_NOTICES.

### Phase 3 (media and waveform) — self-review, 2026-11-25

- [x] Only documented APIs: GSMTC (Windows.Media.Control), CoreAudio via NAudio, AppInfo, `IApplicationActivationManager`, `SHGetFileInfo`, `GetApplicationUserModelId`. No NPSM or other undocumented media APIs.
- [x] No input injection: the source app is brought forward only after the user's click, by restoring its window or documented activation.
- [x] Titles, artists and artwork are never logged or written (log lines carry error types and command names only).
- [x] Other processes are identified by name / AUMID only, with limited query rights.
- [x] NAudio 3.3.0 (MIT, approved) added to IrisAura.Island.Providers; recorded in THIRD_PARTY_NOTICES.
- [x] Browser tests used separate throwaway profiles; the owner's browser profiles were not touched.

### Phase 4 (system providers) — self-review, 2026-11-25

- [x] Only documented APIs: CoreAudio endpoint volume and session state (NAudio), WMI `WmiMonitorBrightnessEvent`,
      `Windows.System.Power.PowerManager`, `DeviceWatcher` / `BluetoothDevice` / `BluetoothLEDevice` / device properties,
      `RegNotifyChangeKeyValue` (read-only watch of the user's consent store), `SHGetKnownFolderPath`, `FileSystemWatcher`,
      `RegisterHotKey`. No undocumented CapabilityAccessManager interfaces, no shell-window manipulation (volume flyout
      hiding deliberately not built, KI-027).
- [x] No input injection. The timer shortcut is a registered hotkey (`Ctrl+Alt+Shift+T`, no Windows key).
- [x] Logs carry provider ids, states and error types only; no device names, app names, file names or levels.
- [x] Persisted: provider on/off flags and the timer's duration/end time; nothing else (PRIVACY.md).
- [x] Downloads: file names and sizes only; file contents are never opened. "Open" launches the Downloads folder via
      the shell after the user's click.
- [x] Call detection never reads audio, window titles or notification text; off by default and labelled beta.
- [x] Notifications not implemented: the sparse package needs a development certificate trusted on the machine (trust
      store change) and package registration; waiting for owner approval (KI-026).
- [x] `--system-probe` diagnostics change volume/brightness by 1 % and restore them, and open the microphone only with
      explicit flags and a visible notice; audio is discarded unread; the report holds no names.
- [x] New package System.Management 10.0.15 (approved in BLUEPRINT §4, MIT), pinned; THIRD_PARTY_NOTICES and
      DEPENDENCIES updated.

### Phase 4B (dashboard panel) — self-review, 2026-11-25

- [x] Camera: off by default; switched on only by the user in the panel; `SharedReadOnly`; runs only while the panel's
      Island tab shows; `MediaCapture` disposed on close/tab change (self-test: exclusive open succeeds afterwards);
      frames never stored, logged or sent; last frame cleared; camera dot on while it runs. Camera code lives in
      IrisAura.Island (not the credential provider, not Face).
- [x] Snapshots (`--snapshots`) render the Island's own visual only; the mirror is never written to disk.
- [x] File tray: paths only, never logged; items are opened only after the user's click (shell default app);
      drag-out uses standard OLE file drag; no elevation, no copies.
- [x] Calendar: no access (dates only).
- [x] Focus: panel open/close and tab changes took no focus (self-test); the panel's buttons work without activation
      (`WS_EX_NOACTIVATE` kept).
- [x] No new packages. Only documented APIs (`Windows.Media.Capture`, `SHGetFileInfo`, WPF drag and drop).

### Phase 5 (Activity SDK) — self-review, 2026-11-25

- [x] Pipe DACL: current user only, NETWORK denied, inheritance off (test reads the ACL); first instance with
      `FILE_FLAG_FIRST_PIPE_INSTANCE`; a squatted name disables the SDK instead of sharing it (test).
- [x] Client identity from the OS (`GetNamedPipeClientProcessId`, `QueryFullProcessImageName`, session check), never
      from the message; ids namespaced per identity; another app cannot see/update/end (tests + e2e).
- [x] Limits: 64 KB lines (connection closed), 20-error disconnect, 10 updates/s/activity, 5 activities/app,
      priority 10–60, TTL ≤ 24 h, ≤ 3 buttons, icon ≤ 128 × 128 from the header before decoding, text sanitised.
- [x] Links: scheme policy, opened only after the user's click (T-27).
- [x] Client verifies the server (name, session, Authenticode with pinned/known publisher); no network in the check;
      development certificate pinned, **not** added to any trusted store (e2e report: `devCertificateInTrustedStores:
      false`).
- [x] Logs: program file names, error codes, counts; no activity text, icons or links.
- [x] New packages for the netstandard2.0 SDK only: System.Text.Json 10.0.15 and System.Security.Principal.Windows
      5.0.0 (approved in BLUEPRINT §4, MIT); DEPENDENCIES and THIRD_PARTY_NOTICES updated.
- [ ] **Independent review** of the SDK pipe before Release 1 (gate above).

### Release 1 (installer, signing, updates) — self-review, 2026-11-26

- [x] Island-only MSI: no Face files, services or credential provider (install test checks all three).
- [x] Installs to `%ProgramFiles%` (admin-only writable; THREAT_MODEL T-13, T-28); per-user data untouched.
- [x] IrisAura binaries and the MSI Authenticode-signed (SHA-256); development certificate in `CurrentUser\My` only.
- [x] Update verification: ECDSA P-256 manifest signature by a non-exportable CNG key, product, no downgrade, size,
      SHA-256, MSI signer; tampered MSI and edited manifest rejected on the installed app; dev key trusted only in
      development builds; no network.
- [x] No WiX custom-action binaries; no elevated code of ours runs during install.
- [ ] Production certificate, RFC 3161 timestamp, SBOM (CycloneDX) and independent review before Release 1 ships.

## Findings

| ID | Date | Component | Finding | Severity | Status |
|---|---|---|---|---|---|
| | | | | | |
