# Local server development

The local ASP.NET Core server composes Urd's existing Core and SQLite; it is not a
second backend. It now exposes **create, Send, receipt lookup, snapshot polling and
content reads**. UI, listing, addressed Stop, static assets, startup recovery and real
providers are not implemented. Queueing and safe-boundary steering are required Core
capabilities still to build, not optional extras or a permanent busy-input restriction.

## Run

Use the pinned SDK and locked restore described in [backend development](backend-development.md).
From `source/`, with `$dotnet` set to that SDK's executable:

```powershell
& $dotnet build Urd.slnx --no-restore --configuration Release --disable-build-servers -m:1 -p:UseSharedCompilation=false
& $dotnet .\hosting\server\bin\Release\net10.0\Urd.Server.dll --data-root 'C:\dev\repos\urd\data' --port 0
```

Choose your own absolute data directory. Both arguments are required; either order is
accepted. Port `0` lets the OS select an available port; an explicit port must be within
`1..65535`. Unknown, duplicate or malformed options fail before opening data.
There is no bootstrap, wrapper script or automatic browser launch in this slice.

After SQLite opens and Kestrel binds, stdout contains exactly one readiness record,
for example:

```json
{"event":"urd-ready","origin":"http://127.0.0.1:56123"}
```

Use the actual printed origin, not this example or a guessed port. Diagnostics go to
stderr. Invalid arguments exit with code `2`; startup/run failures exit with code `1`.
Ctrl+C requests graceful shutdown. The run returns successfully only after its HTTP
and Core shutdown operations both finish; it has no forced cleanup timeout.

## HTTP and local access

- Public `GET /health` or `HEAD /health` returns **204**, without a body or private state.
  Health and API GET requests with a body are rejected with 400. Unknown paths receive
  404 after admission checks; unsupported methods receive 405.
- The listener is explicitly IPv4 `127.0.0.1`. Default appsettings, environment and URL
  configuration providers are not loaded; they cannot add listeners or change the data root.
- Every request requires a loopback peer and Host `127.0.0.1` with the actual served port.
  Omitting the port is valid only for HTTP's default port 80. Forwarded headers are ignored.
- Origin may be absent. If supplied, it must be exactly the canonical served origin;
  foreign, `null`, multiple and noncanonical origins receive 403. There are no CORS
  grants, credentials or cookies.
- Every `/api` request also requires exactly one `X-Urd-Client: browser`, including reads
  and unknown API paths. This non-secret marker blocks ordinary foreign browser requests,
  not native programs. API responses use `Cache-Control: no-store`, including rejections.
- Before readiness and once shutdown admission closes, the gate returns 503. These checks
  restrict ordinary foreign browser access, not trusted native/local programs.

## Session HTTP contract

| Method and path | Request / successful response |
| --- | --- |
| POST `/api/sessions` | Empty JSON object `{}` → 201 `{sessionId}`; Location names its snapshot |
| POST `/api/sessions/{sessionId}/inputs` | `InputCommand` → 200 `InputReceipt`, Accepted **or** Refused |
| GET `/api/sessions/{sessionId}/inputs/{inputId}` | 200 receipt or literal JSON `null` |
| GET `/api/sessions/{sessionId}/snapshot` | 200 `SessionSnapshot` |
| GET `/api/sessions/{sessionId}/content` | 200 `MessageContentPage` |

POST requires JSON. Encoded bodies are limited to 128 KiB, including chunked requests;
decoded input text is limited to 16 KiB of strict UTF-8. Missing required command members,
null text, empty/malformed GUIDs and invalid cursors are rejected before admission.
JSON uses camelCase properties and enum names, string GUIDs and integer revisions.
Content GET requires six query fields: `runId`, `messageId`, `role` (`user` or `assistant`),
`historyRevision`, `segmentIndex`, `utf8Offset`. These directly describe Core's segment-local
cursor; offsets are bytes, not string indices. Snapshot/content bounds and the distinct
closed/final-output/settlement facts are unchanged from [Core](backend-development.md).

In another terminal, set `$origin` to the actual readiness origin, then:

```powershell
$headers = @{ 'X-Urd-Client' = 'browser' }
$chat = Invoke-RestMethod "$origin/api/sessions" -Method Post -Headers $headers -ContentType 'application/json' -Body '{}'
$view = Invoke-RestMethod "$origin/api/sessions/$($chat.sessionId)/snapshot" -Headers $headers
$command = @{ inputId = [guid]::NewGuid().ToString(); expectedHistoryRevision = $view.historyRevision; text = 'Hello' } | ConvertTo-Json
$receipt = Invoke-RestMethod "$origin/api/sessions/$($chat.sessionId)/inputs" -Method Post -Headers $headers -ContentType 'application/json' -Body $command
$receipt # A durable admission decision, not a completed answer.
```

Poll the snapshot to observe committed text and settlement; use content reads beyond previews.
Current Core admits one input per active run; a distinct busy input gets a durable refusal.
The HTTP layer adds no busy policy and does not turn a refusal into an HTTP error. Queueing,
preflight follow-up and safe-boundary steering need their own Core increment before calling
the chat workflow complete.

Errors: 400 malformed input/cursor, 413 oversized body/text, 415 non-JSON POST or unsupported charset,
404 missing session/message, 409 conflicting input identity or stale content revision,
503 shutdown admission/storage failure. Input conflicts are classified by Core's
`InputConflictException`, not exception text or every `InvalidOperationException`.
None of these errors—or an absent receipt—proves an earlier Send was durably refused.
Only a receipt matching the session and entire original command resolves unknown admission.
An explicit retry repeats the exact ID/text/revision; no server mutation retries occur.
Lost creation acknowledgement remains unknown; without listing, this API cannot rediscover
its session ID. Do not automatically create another chat to hide that uncertainty.

## Data and lifetime

`Program.RunServerAsync(ServerOptions, Action<Uri>, CancellationToken)` owns one run.
`ServerOptions` validates paths/ports without I/O. The run creates the explicit directory,
opens `urd.lock` with exclusive sharing, then opens `urd.sqlite` and composes one store,
manager and deterministic streaming `FakeChatClient`. Constructors do not start model work.
The fake client returns `Hello from Urd.`; no health/read request invokes it.
An internal overload accepts a client factory for controlled composition tests. The run
constructs and owns that client inside the same lifetime; CLI behavior is unchanged.

Request cancellation applies to receipt/snapshot/content reads, not accepted model work.
Create/Send handlers return after admission without waiting for model settlement; closing
a connection does not own cancellation of the session.

A second Urd host for that directory fails before opening SQLite. The lock file is retained;
its open handle, not its contents or a PID, conveys ownership. Separate data roots can run
concurrently. This cooperative lease does not prevent another native program from directly
opening SQLite. Never delete a live data directory or its lock file to bypass ownership.

On stop, admission closes first. Core session shutdown and HTTP stop/drain are initiated
and both are awaited without the caller's cancellation token or the host's default timeout.
Only a successful drain permits application/store/client disposal and lease release.
Startup or readiness-callback failures also enter cleanup. A drain failure is reported and
the CLI exits unsuccessfully without explicitly disposing resources beneath uncertain work.
The stop token requests shutdown; it is not a deadline or permission to abandon cleanup.

Normal reopen preserves saved history and does not replay work. Opening a database does
**not** recover orphan runs or make a previously fenced conversation usable again.

## Verification and review

Locked restore and the Release solution suite pass **210/210** tests with no compiler or
analyzer warnings. The original 72 hosting cases covered startup/lifetime; 26 added cases
cover API marker/cache behavior and the five-operation HTTP path. Real Kestrel/SQLite tests
exercise two clients/chats, exact concurrent retries, retained refusal, Unicode content and
cursors, JSON/media/size limits (including chunked bodies), rejection before read/write
side effects, and restart without replay. Child-process tests cover CLI readiness, paths
with spaces, hostile ambient listener configuration and competing-process ownership.
In an isolated copy, those child tests also rejected deliberate regressions that enabled
ambient listener configuration or shared the directory lease; restored sources passed again.
The HTTP route tests also rejected deliberate regressions allowing unmarked private reads
or turning a saved refusal into HTTP 409; the restored nine-test route suite passed again.

The child readiness test explicitly terminates its own process after health checks;
that is **not** evidence of graceful shutdown. Separate in-process tests use the actual
server's stop token and await completion. A gated model now proves that HTTP-admitted work
keeps shutdown, the client and directory lease live until model cleanup releases and
settlement saves. A sender that closes without reading its acknowledgement can retrieve
and retry its retained receipt while the model continues; this does not force a precise
in-flight `RequestAborted` race. Core tests independently cover callbacks and persistence
failures. No browser, load, queue/steering or real-provider claim follows.

Review `source/hosting/server/Program.cs` first, then `ServerOptions.cs`,
`LocalBrowserGate.cs`, `SessionRoutes.cs` and `FakeChatClient.cs`. Tests are in
`source/tests/Urd.Backend.Tests/hosting/`, using `support/RunningServer.cs`,
`support/ServerProcess.cs` and the existing SQLite evidence readers.
The [hosting design record](scratch/initial-ui-hosting-design.md) separates this approval
from future Core/transport/UI work.
