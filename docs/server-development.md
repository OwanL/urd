# Local server development

The local ASP.NET Core server composes Urd's existing Core and SQLite; it is not a
second backend. It now exposes seven operations: **create, list saved primary IDs, Send,
receipt lookup, snapshot polling, content reads and addressed Stop**. The owner-approved
listing slice is implemented and tested; verification evidence is below.
The first [browser screen](browser-development.md) is served from built static assets.
[Provider management](provider-management.md) supports ChatGPT sign-in, OpenAI API keys
and custom-compatible connections; production chat now borrows real provider-managed clients.
Provider setup remains headless, and startup recovery is not implemented.
Core now provides default durable safe-boundary steering
for busy Sends in the owned execution; real tools, tool-inspection UI/API and an explicit after-execution
queue remain deferred.

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
To include the browser, first run `npm run build` in `source/ui/`; the Server build
stages that bundle into `wwwroot`. Backend-only builds need no npm step.

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
- Built HTML/static assets pass the same gate, without the private API marker. HTML uses
  `no-store`; hashed `/assets` files are immutable. Missing assets and unknown API paths
  remain 404, not an HTML fallback.

## Session HTTP contract

| Method and path | Request / successful response |
| --- | --- |
| POST `/api/sessions` | Empty JSON object `{}` → 201 `{sessionId}`; Location names its snapshot |
| GET `/api/sessions?cursor=...` | Optional opaque string cursor → 200 `SessionIdPage` (`sessionIds`, `nextCursor`) |
| POST `/api/sessions/{sessionId}/message-submissions` | `UserMessageSubmission` → 200 `UserMessageReceipt`, Accepted **or** Refused |
| GET `/api/sessions/{sessionId}/message-submissions/{submissionId}` | 200 receipt or literal JSON `null` |
| POST `/api/sessions/{sessionId}/executions/{executionId}/stop` | Empty JSON object `{}` → 200 `StopReceipt` for Requested or Settled |
| GET `/api/sessions/{sessionId}/snapshot` | 200 `SessionSnapshot` |
| GET `/api/sessions/{sessionId}/content` | 200 `MessageContentPage` |

**Breaking vocabulary change:** there are no legacy route/member aliases.
A submission body is `{submissionId, expectedHistoryRevision, text, llmSelection}`.
A receipt is `{sessionId, submission, acceptance, refusalReason, executionId}`, retaining
that entire original submission, including its complete selected connection binding.
`acceptance` is `accepted` or `refused`; accepted receipts have an execution ID, refused
receipts have a refusal reason and null execution ID. Stop receipts and
`SessionExecutionResult` use `executionId`; snapshots use `unsettledExecutionId` and
`MessageRef` (including nested content cursors) uses `executionId`.
The vocabulary cleanup preserved saved IDs, revisions, SQL identifiers and decisions.
Selection adds the schema-8 migration described below; the seven operations retain their
local admission gates, status mapping, no-store and read-only request-cancellation rules.
The naming and structure cleanup passed locked restore and **296/296 Release tests** without warnings,
plus independent acceptance review. Raw HTTP checks verify the renamed receipt, Stop,
snapshot and nested cursor fields, and reject legacy routes and required field names.
Counts below describe the earlier feature checkpoints.

### Frozen LLM selection

New Sends require `llmSelection: {connection, modelId, reasoningEffort}`. `connection`
is the full saved secret-free `ProviderConnection`, including route, API, endpoint
and authentication kind, not credentials. A stale/removed binding is not replaced
by a lookup or fallback. The provider factory runs lazily outside admission/storage
locks; inability to create/use the selected client settles the execution as Failed,
not as a fabricated refused admission.

Null effort requests the provider default. Explicit standard values are `none`,
`low`, `medium`, `high`, `extraHigh`; validation is not a claim that a particular
model supports every effort. The execution freezes the choice through every tool
and steering continuation. Busy steering may tolerate a connection display-name
change, but any operational choice change durably refuses `selectionLocked`.
Idle switching preserves visible history; native-context compatibility remains the
provider layer's responsibility.

Snapshots include `latestLlmSelection`, taken from the initiating submission of the
newest execution even when it is outside the recent message window. It is not a
separate mutable preference. Schema 8 stores nullable selection JSON and widens the
refusal check with a transactional table rebuild and foreign-key verification.
Legacy decisions retain null; their exact retries are resolved before fresh-Send
validation, without inventing a provider choice or replaying work. A reused ID with
changed text, revision **or any original selection member** remains a 409 conflict.

This Core/HTTP checkpoint passed locked restore, independent source/test review and
**583/583 Release tests**, including factory reentrancy/failure, frozen continuations,
selection outside the preview window, actual provider-manager stale bindings with
injected transport, raw HTTP validation/reopen, schema-7 migration and rollback.
No live account/model calls or rendered browser qualification are implied by that
checkpoint.

Listing calls `SessionManager.ListPrimaryIdsAsync` and returns at most 60 IDs in newest-created
order, without handles, summaries, titles, history loading or execution. Empty and retained
unsettled chats are included. Omit `cursor` for the newest page; copy `nextCursor` unchanged
until null. It is an opaque positive Int64 ordinal encoded as an invariant decimal string,
not a JSON number. Supplied empty, malformed, nonpositive or overflowing cursors return 400;
a valid cursor with no remaining entries returns 200 with an empty page, not 404.
Boundary deletion does not invalidate continuation; new creations require refreshing page one.
Pages are per-query committed views, not a frozen multi-page snapshot. The existing local
loopback/Host/Origin/client-marker/no-store gate and no-body GET rule apply; shutdown/storage
failures remain 503. Core's schema-5 ordering metadata and atomic create are described in
[backend development](backend-development.md#saved-primary-id-discovery).
Legacy rows use a fixed descending binary-ID fallback, not recovered creation chronology.
No last-used tracking, full-history navigation or automatic recovery is added.

POST requires JSON. Encoded bodies are limited to 128 KiB, including chunked requests;
decoded submission text is limited to 16 KiB of strict UTF-8. Missing required submission members,
null text, empty/malformed GUIDs and invalid cursors are rejected before admission.
JSON uses camelCase properties and enum names, string GUIDs and integer revisions.
Content GET requires six query fields: `executionId`, `messageId`, `role` (`user` or `assistant`),
`historyRevision`, `segmentIndex`, `utf8Offset`. These directly describe Core's segment-local
cursor; offsets are bytes, not string indices. Snapshot/content bounds and the distinct
closed/final-output/settlement facts are unchanged from [Core](backend-development.md).

In another terminal, set `$origin` to the actual readiness origin and choose
`$connectionId` and `$modelId` from your configured provider/catalog. This sends a
real request and may incur provider usage; use the isolated browser fixture for
offline checks instead.

```powershell
$headers = @{ 'X-Urd-Client' = 'browser' }
$chat = Invoke-RestMethod "$origin/api/sessions" -Method Post -Headers $headers -ContentType 'application/json' -Body '{}'
$view = Invoke-RestMethod "$origin/api/sessions/$($chat.sessionId)/snapshot" -Headers $headers
$connection = Invoke-RestMethod "$origin/api/providers/$connectionId" -Headers $headers
$selection = @{ connection = $connection; modelId = $modelId; reasoningEffort = $null }
$submission = @{ submissionId = [guid]::NewGuid().ToString(); expectedHistoryRevision = $view.historyRevision; text = 'Hello'; llmSelection = $selection } | ConvertTo-Json -Depth 8
$receipt = Invoke-RestMethod "$origin/api/sessions/$($chat.sessionId)/message-submissions" -Method Post -Headers $headers -ContentType 'application/json' -Body $submission
$receipt # A durable admission decision, not a completed answer.
```

Poll the snapshot to observe committed text and settlement; use content reads beyond previews.
A distinct busy Send can receive an Accepted receipt for durable steering in the owned execution,
up to eight nonterminal input deliveries. At the next safe request boundary, tool results
precede separate user messages in admission order; pending steering also continues after a
successful no-tool response. Already-claimed requests are unchanged. The HTTP layer adds no
busy policy or steering-specific routes and does not turn a durable refusal into an HTTP error.
Public reads remain user/assistant text only; there is no tool-inspection endpoint.

Stop addresses the observed execution ID, never a replacement: `Requested` acknowledges committed
intent and pending-steering retirement, not finished cleanup; `Settled` includes the saved
outcome. Retrying reports current status. Missing/wrong-session executions return 404; an unresolved
unowned execution returns 409 with an `Unavailable` receipt, without mutation or fake settlement.
Poll existing snapshots for stopping/fencing and saved settlement; no completion endpoint was added.
Core initiates local cancellation even if the Stop write fails, fences admission and returns
503 rather than a false durable acknowledgement. Local stopping also blocks late output,
successful request completion and new LLM/tool work; snapshots include that local intent,
not a promise it was saved. Actual tool-result evidence may still be persisted.
The driver joins cancellation callbacks and LLM/tool cleanup, not the HTTP request.
A held cleanup remains visibly stopping; callback faults leave fenced/unsettled work.

Errors: 400 malformed input/cursor, 413 oversized body/text, 415 non-JSON POST or unsupported charset,
404 missing session/message/execution, 409 conflicting submission identity, stale content revision or unavailable Stop,
503 shutdown admission/storage failure. Submission conflicts are classified by Core's
`UserMessageConflictException`, not exception text or every `InvalidOperationException`.
None of these errors—or an absent receipt—proves an earlier Send was durably refused.
Only a receipt matching the session and entire original submission resolves unknown admission.
An explicit retry repeats the exact ID/text/revision/selection; no server mutation retries occur.
Lost creation acknowledgement remains unknown. Listing can rediscover saved IDs, but cannot
associate any returned ID with that unacknowledged create or justify automatic retry.
Do not automatically create another chat to hide that uncertainty.

## Data and lifetime

`Program.RunServerAsync(ServerOptions, Action<Uri>, CancellationToken)` owns one run.
`ServerOptions` validates paths/ports without I/O. The run creates the explicit directory,
opens `urd.lock` with exclusive sharing, then opens `urd.sqlite` and composes one store,
session manager and provider manager. Production sessions borrow
`ProviderManager.CreateChatClient(expectedBinding)` clients, without disposing them.
Constructors and health/read requests do not start LLM work. An internal overload
accepts an owned client factory only for controlled composition tests; it is not a
CLI fake-provider mode. Those clients are disposed only after successful drain.

Request cancellation applies to listing/receipt/snapshot/content reads, not accepted LLM work.
Listing's `RequestAborted` owns only that bounded read, never execution; cancellation may abandon
its guard wait or precede the synchronous read, not interrupt an in-progress SQLite query.
Create/Send handlers return after admission and Stop after intent acknowledgement, without waiting
for settlement. Stop body validation may use request cancellation, but no `RequestAborted` token
enters Core Stop; closing a connection does not own cancellation of the session.

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
**not** recover orphan executions or make a previously fenced conversation usable again.
Stop retires pending steering without rewriting Accepted receipts or automatically carrying
it into another execution. Separate independent worker chats continue; bounded-descendant ownership
requirements remain in [architecture](ARCHITECTURE.md) and [session design](SESSION-DESIGN.md),
not a new child runtime. Stop cannot undo effects or forcibly kill arbitrary in-process functions.
The CLI configures real selected provider clients, but not application tools. Public
Stop does not add a second execution loop or change provider/tool ownership.

## Verification and review

The listing slice passed locked restore, independent review and **295/295 Release tests**, without
warnings. Real Kestrel/SQLite tests cover multiple pages, another client, reopen/cursor reuse,
invalid cursors, local gates/no-store/no-body rules and storage failures, with no LLM calls.
Core/storage tests cover migration/rollback, no hydration/replay and cancellation isolation;
see [backend evidence](backend-development.md#saved-primary-id-discovery). No UI qualification is implied.

The historical public Stop slice passed locked restore, independent review and the full **281/281 Release tests**,
without warnings. `SessionStopRoutesTests` exercises real Kestrel/SQLite acknowledgements while
callbacks/cleanup remain held, unread acknowledgements, repeated and stale Stops, independent
sessions, orphan 409 responses, failed persistence with local cancellation, and validation/gates.
Core tests cover uncooperative fake tools, late-write rejection, rollback and callback failures.
These do not qualify real tools, subprocess termination or subagent-runtime cancellation.
The previous steering/fake-tool checkpoint passed locked restore and the full Release
solution suite: **259/259** tests, with no warnings. Its real Kestrel/SQLite
`BusyHttpSendsBecomeDurableSteeringWithoutChangingTheRoutes` case verifies Accepted busy
Sends, receipt lookup/exact retry and ordered continuation in the same execution through the
existing routes. Core tests cover fake-tool execution and lifecycle/fault boundaries;
HTTP does not expose tool inspection or configure real tools.

### Historical hosting evidence

The earlier five-operation HTTP slice passed **210/210** tests with no compiler or
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
server's stop token and await completion. A gated LLM now proves that HTTP-admitted work
keeps shutdown, the client and directory lease live until LLM cleanup releases and
settlement saves. A sender that closes without reading its acknowledgement can retrieve
and retry its retained receipt while the LLM continues; this does not force a precise
in-flight `RequestAborted` race. Core tests independently cover callbacks and persistence
failures. That earlier evidence did not qualify queue/steering. Neither it nor the current
fake-tool checkpoint qualifies browser behavior, load or real tools/providers.

Review `source/hosting/server/Program.cs` first, then `ServerOptions.cs`,
`LocalBrowserGate.cs`, `SessionRoutes.cs` and `ProviderRoutes.cs`. The fake client lives
only in test support. Tests are in
`source/tests/Urd.Backend.Tests/hosting/`, using `support/RunningServer.cs`,
`support/ServerProcess.cs` and the existing SQLite evidence readers. In particular,
`SessionRoutesTests.cs` includes the busy-Send steering flow; `SessionHttpLifetimeTests.cs`
retains the active-work shutdown and disconnected-sender checks.
The [hosting design record](scratch/initial-ui-hosting-design.md) separates this approval
from future Core/transport/UI work.
