# IrisAura Island Activity SDK — protocol v1

Lets any local app or script show its own activity on IrisAura Island: progress, a status, up to three buttons,
and a link that opens when the user clicks it (BLUEPRINT §8.6.14). Implemented in Phase 5; ADR 0009 records the
design decisions.

Three ways to use it:

| You have | Use |
|---|---|
| A .NET app (.NET Framework 4.6.1+, .NET Core, .NET 5+) | `IrisAura.Island.Sdk` (netstandard2.0) |
| A script in any language | `irisaura-island` command line (ships with IrisAura Island) |
| Anything else | Write newline-delimited JSON to the pipe yourself |

## Quick start

### .NET

```csharp
using IrisAura.Island.Sdk;

using var island = await IslandClient.ConnectAsync(new IslandClientOptions { AppName = "Builder" });
island.ActionReceived += (_, e) => Console.WriteLine($"{e.ActivityId}: {e.Button}");   // button presses

var spec = new IslandActivitySpec("build-42", "Deploying API", progress: 0.1) { TrailingText = "10%" };
spec.Buttons.Add(new IslandButton("cancel", "Cancel"));
var act = await island.StartAsync(spec);
await act.UpdateAsync(progress: 0.5, trailingText: "50%");
await act.EndAsync("Deployed ✓");
```

Every `StartAsync` / `UpdateAsync` / `EndAsync` waits for the Island and throws `IslandSdkException` (with `Code`)
if it refused. A runnable example is in [samples/IrisAura.Island.SampleApp](../samples/IrisAura.Island.SampleApp).

### Command line

```powershell
irisaura-island push   --app Backup --id nightly --title "Backing up photos" --progress 0.3 --text 30% --button stop=Stop
irisaura-island update --app Backup --id nightly --progress 0.8 --text 80%
irisaura-island watch  --app Backup            # prints "nightly stop" when the user presses Stop (Ctrl+C to quit)
irisaura-island end    --app Backup --id nightly --final-text "Done ✓"
irisaura-island list   --app Backup            # your live activity ids
```

Exit codes: `0` done · `1` usage error · `2` IrisAura Island not running (or "Apps using the IrisAura SDK" switched
off) · `3` the pipe server is not a trusted IrisAura Island · `4` refused (the message says why).

## Who can connect, and who the Island thinks you are

- **Pipe:** `\\.\pipe\IrisAura.Island.Activities.<UserSID>` (`IrisAura.Contracts.PipeNames.IslandActivities`).
  Its DACL allows only the current user; the NETWORK group is denied and remote clients are rejected. The Island
  creates it with `FILE_FLAG_FIRST_PIPE_INSTANCE`: if another process already holds the name, the Island refuses to
  share it and shows the SDK as unavailable.
- **Your identity** is your **program path** (read by the Island from the connected process, not claimed by you) plus
  the **app name** you send. Activities, limits and blocking are per identity: another program, or the same program
  under another app name, cannot see, update or end your activities. A client in another Windows session is refused.
- **Lifetime:** activities stay after you disconnect (a script can push and exit) until their TTL (default 1 hour,
  at most 24 hours), `end`, or the user blocks your app. A new connection with the same identity takes over (updates
  and button presses).
- **Blocking:** right-click the Island → **External apps** lists apps that used the SDK; untick one to block it (its
  activities disappear and it gets `blocked`). The choice is saved.

## Checking the server (pipe squatting)

Before sending anything the client checks who serves the pipe (`IslandClientOptions.Verification`):

| Mode | Check | Use |
|---|---|---|
| `SignedIsland` (default) | `IrisAura.Island.exe`, same session, Authenticode signature intact, signer is a known IrisAura publisher **or** a pinned thumbprint (`TrustedThumbprints`). A pinned certificate is accepted even if it does not chain to a trusted root (development certificate), but the file's signature must verify. | Everything |
| `UnsignedDevelopmentBuild` | `IrisAura.Island.exe`, same session; no signature check | Unsigned developer builds only |
| `None` | nothing | Tests only |

Signature checks never go to the network (no revocation download). The production publisher is added when the
code-signing certificate exists (Phase 9); until then use a pinned thumbprint. Command line: `--trust-thumbprint SHA1`
(repeatable) or `--allow-unsigned-island`.

## Wire protocol

- Newline-delimited UTF-8 JSON, one object per line, camelCase names. Unknown fields are ignored.
- **Max 64 KB per line**; a longer line closes the connection.
- Every message has `"v": 1`; other versions get `bad-version`.
- The Island answers only errors and queries, **in order**. To confirm a request, follow it with `query`: an error
  for the request arrives before the `activities` answer (the client library does this).
- 20 errors in a row close the connection.

### App → Island

```json
{ "v": 1, "op": "start", "id": "build-42", "app": "Builder", "priority": 40,
  "compact":  { "leadingIcon": "data:image/png;base64,...", "leadingText": "Builder", "trailingText": "37%", "trailingProgress": 0.37 },
  "expanded": { "title": "Deploying API", "subtitle": "Stage 3 of 5", "progress": 0.37,
                "buttons": [ { "id": "cancel", "label": "Cancel" } ] },
  "openUri": "myapp://builds/42", "ttlSeconds": 3600 }
{ "v": 1, "op": "update", "id": "build-42", "compact": { "trailingText": "80%" }, "expanded": { "progress": 0.8 } }
{ "v": 1, "op": "end",    "id": "build-42", "finalText": "Deployed ✓", "dismissAfterSeconds": 3 }
{ "v": 1, "op": "query" }
```

- `start` replaces an activity with the same id; `update` changes only the fields it sends (a rejected update
  changes nothing); `app` may be sent with any message and defaults to the program name.
- If there is no `trailingText`, `trailingProgress` draws a ring.

### Island → app

```json
{ "v": 1, "op": "hello", "server": "IrisAura.Island/1.0.0.0" }
{ "v": 1, "op": "action", "id": "build-42", "button": "cancel" }
{ "v": 1, "op": "activities", "ids": [ "build-42" ] }
{ "v": 1, "op": "error", "id": "build-42", "code": "rate-limited", "message": "..." }
```

`action` with `"button": "open"` means the user clicked an activity that has no `openUri` and the Island could not
bring your window forward.

### Error codes

`bad-json`, `bad-version`, `bad-request` (invalid value; the message says which), `not-found` (never started, ended,
expired, or not yours), `rate-limited`, `too-many-activities`, `blocked`, `disabled` (the user switched the SDK off).

## Limits and rules

| Rule | Value |
|---|---|
| Message | 64 KB per line |
| Activity id | 1–64 of `A–Z a–z 0–9 . _ -` |
| Activities per app | 5 (expired ones do not count) |
| Updates | 10 per second per activity (start counts); extra ones are dropped with `rate-limited` |
| Priority | 10–60 (default 30; system activities such as calls and timers can still win) |
| TTL | default 3600 s, 1–86400 s |
| Icon | `data:image/png` or `data:image/jpeg` base64, at most 128 × 128 |
| Buttons | up to 3; id 1–32 of the id alphabet, not `open`; label up to 24 characters |
| Text | plain text; control, bidi-override and zero-width characters removed; title 80, subtitle 120, trailing 24, leading 40, final 40 characters |
| `openUri` | `http`, `https` or your app's own scheme; `file`, script, search, troubleshooter and other system schemes are refused; opened only when the user clicks |

## Privacy

The Island keeps SDK activities in memory only and never logs their text, icons or links (logs carry program file
names and error codes). Only the list of blocked apps is saved (`island-settings.json`).
