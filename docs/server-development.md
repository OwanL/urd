# Local server development

The local ASP.NET Core server composes Urd's Core, SQLite and provider manager; it is not a
second backend. Session routes cover creation, saved-ID listing, Send/receipt, addressed Stop,
slim snapshots, paged transcript history, not-sent inputs and bounded content reads. Separate
event routes provide server-sent updates for the browser workspace. Provider management and
its settings UI are integrated; production sessions borrow provider-managed clients. Startup
records interrupted work without replay; no explicit after-execution queue is implemented.
Tool and background-completion rows expose compact status, not argument/result inspection.

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
On Windows, file/shell tools and pending-call controls are enabled by default and require
Git for Windows. Other platforms retain tool-free startup; explicitly requesting tools is
unsupported. Without `--working-directory`, new Windows chats use the launch drive's root
(for example `C:\`), not the launch folder or data root. Add
`--working-directory 'C:\dev\repos\your-project'` to override this relative-path default.
It is not a sandbox: tools have the server user's full access, without per-call approvals.
New chats save this folder; reopening preserves it. Existing chats without a folder or
with an empty frozen tool list remain readable and tool-free; create a new chat to use tools.
Optional `--git-bash`
selects an absolute Git for Windows Bash executable, independently of `--working-directory`;
otherwise Urd locates Git Bash. Shells receive a startup snapshot of the server environment
and run non-login `bash -c`.

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
| POST `/api/sessions/{sessionId}/compactions` | `CompactionSubmission` → 200 `CompactionReceipt`; 409 when admission is unavailable |
| GET `/api/sessions/{sessionId}/compactions/operations/{operationId}` | 200 receipt or literal JSON `null` |
| GET `/api/sessions/{sessionId}/compactions/{compactionId}` | `expectedHistoryRevision` → 200 `CompactionSummary`, or 404 |
| GET `/api/sessions/{sessionId}/snapshot` | 200 slim `SessionSnapshot` |
| GET `/api/sessions/{sessionId}/history` | Mode/revision and optional entry anchor → 200 bounded `TranscriptHistoryPage` |
| GET `/api/sessions/{sessionId}/not-sent?cursor=...` | Optional opaque cursor → 200 page of known-not-sent inputs |
| GET `/api/sessions/{sessionId}/content` | Segment-local cursor → 200 `MessageContentPage` |
| GET `/api/sessions/{sessionId}/positions` | `expectedHistoryRevision` → 200 `TranscriptPositionSummary` |
| GET `/api/sessions/{sessionId}/positions/locate` | `expectedHistoryRevision`, `fraction` (0–1) → 200 `TranscriptPositionWindow` |
| GET `/api/sessions/{sessionId}/positions/entries/{executionId}/{entryId}` | `expectedHistoryRevision`, `kind` → 200 positioned entry window |
| GET `/api/sessions/{sessionId}/positions/prompts` | `expectedHistoryRevision`, optional `afterRank` and `limit` (1–60) → 200 `TranscriptPromptPage` |
| GET `/api/sessions/{sessionId}/positions/prompts/{executionId}/{entryId}` | `expectedHistoryRevision` → 200 positioned user-prompt window |

[`SessionRoutes.cs`](../source/hosting/server/SessionRoutes.cs) owns the route inventory;
[`SessionClient.ts`](../source/ui/sessions/SessionClient.ts) supplies the browser query shapes.

### Browser event stream

`GET /api/events` opens a transient server-sent event stream. Its `connected` event provides
stream and process-boot IDs. Register a page's open and watched session IDs with
`POST /api/events/{streamId}` using `{open, watched}`; the server emits `status` for open
sessions, `change` for watched sessions, and `session-created` hints. Heartbeats keep the
stream active. Status events update tab status; change notices prompt bounded snapshot/history
reads. Events are not durable history or message payloads. Reconnect performs saved-ID
discovery again. The existing local API marker and gates apply.

**Breaking vocabulary change:** there are no legacy route/member aliases.
A submission body is `{submissionId, expectedHistoryRevision, text, llmSelection}`.
A receipt is `{sessionId, submission, acceptance, refusalReason, executionId}`, retaining
that entire original submission, including its complete selected connection binding.
`acceptance` is `accepted` or `refused`; accepted receipts have an execution ID, refused
receipts have a refusal reason and null execution ID. Stop receipts and
`SessionExecutionResult` use `executionId`; snapshots use `unsettledExecutionId` and
`MessageRef` (including nested content cursors) uses `executionId`.
The vocabulary cleanup preserved saved IDs, revisions, SQL identifiers and decisions.
Schema 11 stores frozen selection, transcript metadata, request usage, known send-attempt
provenance and not-sent tracking. Earlier pre-release schemas must be reset, not migrated. Session routes retain
their local admission gates, status mapping, no-store and read-cancellation rules. Historical
wire checks covered the renamed receipt, Stop, snapshot and cursor fields and rejected legacy
routes and required field names.

### Frozen LLM selection

New Sends require `llmSelection`; [`LlmSelection.ts`](../source/ui/providers/LlmSelection.ts)
owns its browser shape and [`LlmSelection.cs`](../source/backend/harness/providers/LlmSelection.cs)
owns validation. It includes connection, model ID, reasoning effort, family, image support
and a nullable context window. `connection` is the full saved secret-free `ProviderConnection`,
including route, API, endpoint and authentication kind, not credentials. A stale/removed binding is not replaced
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
newest execution, independent of the transcript page being viewed. It is not a separate
mutable preference. Receipt selection JSON is required; an empty session still reports null.
Exact retries resolve the original decision without replaying work. A reused ID with
changed text, revision **or any original selection member** remains a 409 conflict.

Historical Core/HTTP verification covered factory reentrancy/failure, frozen continuations,
actual provider-manager stale bindings with injected transport, raw HTTP validation/reopen,
and schema migration/rollback. This is not live account/model or browser qualification.

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
The listing adds no last-used tracking or automatic recovery. Transcript history has a separate
bounded API; the browser navigates the bounded transcript and loads visible content on demand.

POST requires JSON. Encoded bodies are limited to 128 KiB, including chunked requests;
Core no longer applies the old separate 16 KiB submission-text cap. Missing required submission members,
null text, empty/malformed GUIDs and invalid cursors are rejected before admission.
JSON uses camelCase properties and enum names, string GUIDs and integer revisions.
History GET requires `mode` and `expectedHistoryRevision`; `before`, `after` and `around` also
require the exact `executionId`, `entryId` and `kind` anchor. Pages contain at most 60 entry
metadata records, not text bodies. Not-sent GET accepts an optional opaque positive-ordinal
cursor and returns bounded previews; the browser retrieves the retained receipt before offering
an explicit resend. Content GET requires `executionId`, `messageId`, `role` (`user` or `assistant`),
`historyRevision`, `segmentIndex` and `utf8Offset`. Offsets are bytes, not string indices.
Snapshots contain slim current facts; history metadata and content use their own bounded reads.
Closed, final-output and settlement facts remain distinct; see [Core](backend-development.md).

In another terminal, set `$origin` to the actual readiness origin and choose
`$connectionId` and `$modelId` from your configured provider/catalog. This sends a
real request and may incur provider usage; use the isolated browser fixture for
offline checks instead.

```powershell
$headers = @{ 'X-Urd-Client' = 'browser' }
$chat = Invoke-RestMethod "$origin/api/sessions" -Method Post -Headers $headers -ContentType 'application/json' -Body '{}'
$view = Invoke-RestMethod "$origin/api/sessions/$($chat.sessionId)/snapshot" -Headers $headers
$connection = Invoke-RestMethod "$origin/api/providers/$connectionId" -Headers $headers
$selection = @{ connection = $connection; modelId = $modelId; reasoningEffort = $null; family = 'other'; supportsImages = $false }
$submission = @{ submissionId = [guid]::NewGuid().ToString(); expectedHistoryRevision = $view.historyRevision; text = 'Hello'; llmSelection = $selection } | ConvertTo-Json -Depth 8
$receipt = Invoke-RestMethod "$origin/api/sessions/$($chat.sessionId)/message-submissions" -Method Post -Headers $headers -ContentType 'application/json' -Body $submission
$receipt # A durable admission decision, not a completed answer.
```

Status events update tab status; change notices trigger snapshot/history reads. Read transcript
metadata through `/history` and message bytes through `/content`.
A distinct busy Send can receive an Accepted receipt for durable steering in the owned execution,
up to eight nonterminal input deliveries. At the next safe request boundary, tool results
precede separate user messages in admission order; pending steering also continues after a
successful no-tool response. Already-claimed requests are unchanged. The HTTP layer adds no
busy policy or steering-specific routes and does not turn a durable refusal into an HTTP error.
History entries include tool names and execution status, but there is no endpoint for tool
arguments or results.

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

Request cancellation applies to bounded session/provider reads, plus event-stream lifetime and
event registration. It does not cancel accepted LLM work. A read's `RequestAborted` owns only
that read; cancellation may abandon its guard wait or precede the synchronous SQLite read, not
interrupt a query already in progress. Create/Send return after admission and Stop after intent
acknowledgement, without waiting for settlement. Stop body
validation may use request cancellation, but no `RequestAborted` token enters Core Stop; closing
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

Normal reopen preserves saved history and does not replay work. Opening a store alone does
**not** recover orphan executions. Host startup explicitly recovers them under the directory
lease before creating sessions: close interrupted requests/runs, distinguish unstarted calls
from uncertain effects and preserve committed results. Only a later user instruction starts
new work; missing results are never repaired by rerunning tools.
Stop retires pending steering without rewriting Accepted receipts or automatically carrying
it into another execution. Separate independent worker chats continue; bounded-descendant ownership
requirements remain in [architecture](ARCHITECTURE.md) and [session design](session-design.md),
not a new child runtime. Stop cannot undo effects or forcibly kill arbitrary in-process functions.
The CLI configures real selected provider clients and application tools. Public
Stop cancels provider/foreground/background calls and joins cleanup through the single driver;
processes retained by completed shell calls survive Stop until chat/backend shutdown.

## Verification and review

Current ownership and deliberate coverage reductions are listed in
[the test scope](testing.md). Route-level checks use real Kestrel/SQLite for session,
provider and event contracts; they do not imply live provider calls or qualification of
arbitrary tools, subprocess termination or subagent-runtime cancellation. Earlier hosting
checkpoints covered startup/lifetime, local admission, retries, content cursors, request
limits and shutdown draining. Their test totals and five-operation route description are
historical and are omitted here.

Review `source/hosting/server/Program.cs` first, then `ServerOptions.cs`,
`LocalBrowserGate.cs`, `SessionRoutes.cs`, `EventRoutes.cs` and `ProviderRoutes.cs`.
Tests are in `source/tests/Urd.Backend.Tests/hosting/`; `SessionRoutesTests.cs`,
`TranscriptHistoryRoutesTests.cs` and `SessionEventRoutesTests.cs` cover current session,
history and event routes. `SessionHttpLifetimeTests.cs` covers host drain and retained
receipt reads while accepted work continues. The current route inventory is above;
old hosting proposals are recoverable from Git history.
