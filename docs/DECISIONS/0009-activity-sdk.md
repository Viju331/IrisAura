# 0009 — Activity SDK: identity, lifetime, confirmation and server verification

- **Status:** accepted
- **Date:** 2026-11-25
- **Phase:** 5
- **Blueprint sections:** §8.6.14, §9.1 (T-01, T-02), §10 Phase 5, §12.4

## Context

The SDK lets other local processes put activities on the Island over a per-user named pipe. Any process of the
same user can connect, so the Island must keep apps apart, bound their impact and show only safe content; clients
must not hand data to a process squatting on the pipe name. The protocol is newline JSON so scripts can use it, and
the blueprint's example (`push` from a CLI) implies activities that outlive a short-lived connection.

## Decision

1. **Identity = program path read from the OS + declared app name.** `GetNamedPipeClientProcessId` →
   `QueryFullProcessImageName`; clients in another session are refused. Engine ids are namespaced
   `sdk:<8-hex hash of identity>:<id>`, so one app can never address another's activity (it gets `not-found`).
   One program can act for several named apps (the CLI serves many scripts); the user can block per identity.
2. **Activities outlive connections**, ending at TTL (default 1 h, max 24 h), `end` or a block. The newest
   connection of an identity receives its button presses; with none connected, presses are not delivered.
3. **No acknowledgements on success; replies are ordered.** The client confirms a request by following it with
   `query`; an error for the request arrives before the `activities` answer. Scripts that do not care can just write
   lines.
4. **Pipe security:** DACL current user only + NETWORK denied, protected from inheritance; first instance created
   with `FILE_FLAG_FIRST_PIPE_INSTANCE` (a squatted name makes the provider unavailable, logged as possible
   squatting); 16 concurrent clients; 64 KB lines; 20 consecutive errors disconnect; slow readers are dropped after a
   2 s write timeout.
5. **Content rules** in one pure class (`SdkRequestHandler`): priority 10–60, 5 activities and 10 updates/s per
   activity, text sanitising (control, bidi-override and zero-width characters removed, lengths capped), icons
   checked from the PNG/JPEG header (≤ 128 × 128) before WPF ever decodes them, up to 3 buttons, an allow-by-default
   scheme policy for `openUri` with a deny list of code-running and file schemes; links open only on the user's click.
   Rejected updates leave the activity unchanged.
6. **Client verification (`SignedIsland`, default):** the server must be `IrisAura.Island.exe` in the caller's session
   with an intact Authenticode signature from a known publisher or a **pinned thumbprint**. A pinned certificate is
   accepted when `WinVerifyTrust` returns only `CERT_E_UNTRUSTEDROOT`, so the development certificate works without
   adding it to any trusted store. No network: revocation is not checked (`WTD_REVOKE_NONE`,
   `WTD_CACHE_ONLY_URL_RETRIEVAL`). The production publisher list is empty until the certificate is bought (Phase 9).
   `UnsignedDevelopmentBuild` (name and session only) exists for unsigned developer builds and is never the default.
7. **CLI is hand-parsed** (no `System.CommandLine` dependency) with stable exit codes.

## Consequences

- Evidence: 38 SDK tests (protocol rules, untrusted apps, malformed and oversized messages, error flood, rate limit,
  reconnect, blocking, squatting, fake server, pipe DACL) and `scripts/sdk-e2e.ps1` against a development-signed
  Island (10/10 checks, `docs/evidence/phase-5-sdk-e2e-2026-11-25.json`).
- Accepted risks (same user = same trust level, THREAT_MODEL T-28): a hostile same-user process can open up to 16
  connections, run under an app name of its choice from its own program, or replace a user-writable
  `IrisAura.Island.exe` — the last is closed by installing to `%ProgramFiles%` and signing (Phase 9).
- Until Phase 9, everyone outside the development team must pin a thumbprint or use the development mode.

## Alternatives considered

| Option | Why not |
|---|---|
| End activities on disconnect | Breaks the CLI `push` use case |
| Ack every message | Doubles traffic for scripts that do not need it; ordered errors + `query` give the same certainty |
| Identity from the declared `app` only | Any process could claim another's name |
| Require a chain-trusted signature only | Any CA-signed program named IrisAura.Island.exe would pass; pinning/publisher names are needed |
| Trust the dev certificate machine-wide for testing | Changes the trust store (security boundary); pinning tests the same code path |
| `System.CommandLine` for the CLI | One more dependency for six commands |
