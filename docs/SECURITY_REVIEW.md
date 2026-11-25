# Security Review

Record of security reviews and gate sign-offs (BLUEPRINT §7.8, §9, §10). A gate passes only with a
named reviewer, a date and linked evidence. Independent review means someone other than the author.

## Gates

| Gate | Phase | Required before | Reviewer | Date | Result | Evidence |
|---|---|---|---|---|---|---|
| Pre-logon camera feasibility (A/B/C outcome) | 0B | Phase 8 | | | ⏳ | |
| Credential Provider feasibility + recovery | 0C | Phase 8 | | | ⏳ | |
| Account-type support matrix + credential lifecycle design | 0C | Phase 8 | | | ⏳ | |
| Island SDK pipe security (ACL, squatting, impersonation, rate limits) | 5 | Release 1 | | | ⏳ | |
| Presentation-attack evaluation methodology | 7 | Phase 8 | | | ⏳ | |
| Independent security review of sign-in prototype | 8 | Any external pilot | | | ⏳ | |
| Legal / privacy review | 8, 9 | Any external pilot / release | | | ⏳ | |
| Release security checklist (signing, SBOM, update verification) | 9 | Each release | | | ⏳ | |

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

## Findings

| ID | Date | Component | Finding | Severity | Status |
|---|---|---|---|---|---|
| | | | | | |
