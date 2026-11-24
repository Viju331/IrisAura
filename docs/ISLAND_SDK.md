# IrisAura Island Activity SDK — protocol v1 (DRAFT)

> **Draft.** Implemented and frozen in Phase 5 (BLUEPRINT §8.6.14). Until then this describes the
> intended contract; nothing listens on the pipe yet.

Lets any local app (or script) show its own activity on IrisAura Island.

## Transport

- Named pipe `\\.\pipe\IrisAura.Island.Activities.<UserSID>` — the current user's SID.
  In code: `IrisAura.Contracts.PipeNames.IslandActivities(sid)`.
- ACL: the current user only. The Island creates the pipe with `FILE_FLAG_FIRST_PIPE_INSTANCE`.
- Framing: newline-delimited UTF-8 JSON, one object per line. Max 64 KB per line; longer closes the connection.
- Every message has `"v": 1`. Unknown versions get an error reply.

## Messages

```json
{ "v": 1, "op": "start",  "id": "build-42", "app": "MyApp", "priority": 40,
  "compact": { "leadingIcon": "data:image/png;base64,...", "trailingText": "37%" },
  "expanded": { "title": "Deploying API", "subtitle": "Stage 3 of 5", "progress": 0.37,
                "buttons": [ { "id": "cancel", "label": "Cancel" } ] },
  "openUri": "myapp://builds/42", "ttlSeconds": 3600 }
{ "v": 1, "op": "update", "id": "build-42", "compact": { "trailingText": "80%" }, "expanded": { "progress": 0.8 } }
{ "v": 1, "op": "end",    "id": "build-42", "finalText": "Deployed ✓", "dismissAfterSeconds": 3 }
```

Island → app (button press): `{ "v": 1, "op": "action", "id": "build-42", "button": "cancel" }`

## Limits

| Rule | Value |
|---|---|
| Priority | capped at 60 |
| Images | max 128 × 128 |
| Update rate | 10 updates/s per activity |
| Activities per app | 5 |
| Text | sanitised; no markup or scripts |

## Client library and CLI (Phase 5)

```csharp
await using var island = await IslandClient.ConnectAsync();
var act = await island.StartAsync(new ActivitySpec("build-42", "Deploying", progress: 0.1));
await act.UpdateAsync(progress: 0.5);
await act.EndAsync("Deployed ✓");
```

```powershell
irisaura-island push --id build-42 --title "Deploying" --progress 0.5
```

`IrisAura.Island.Sdk` targets netstandard2.0, so .NET Framework apps can use it. Before sending,
it verifies that the pipe server is the signed `IrisAura.Island.exe` in the caller's session.
