# Local server development

The local ASP.NET Core server composes Urd's Core, SQLite and provider manager; it is not a
second backend. Session routes cover creation, saved-ID listing, Send/receipt, addressed Stop,
slim snapshots, paged transcript history, steering removal, manual Continue, not-sent inputs and bounded content reads. Separate
event routes provide server-sent updates for the browser workspace. Provider management and
its settings UI are integrated; production sessions borrow provider-managed clients. Startup
records interrupted work without replay; no explicit after-execution queue is implemented.
Tool and background-completion rows expose compact labels and status, with model-facing
argument/result inspection loaded separately on demand.

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
unsupported. New sessions use the saved working-directory preference; when unset, new
sessions use the process working drive's root (for example `C:\`), not the launch folder or
data root. Set it in Settings → General, or with `POST /api/preferences/working-directory` using
`{ "workingDirectory": "C:\\dev\\repos\\your-project" }`; `GET /api/preferences` reads the
effective path, including the root fallback. Posting `null` resets the default to that root.
The path must be an existing absolute directory. Working-directory preference changes apply only to new sessions.

`GET /api/preferences` also includes nullable `compactionTriggerTokenCount`.
`POST /api/preferences/compaction-trigger` accepts `{ "compactionTriggerTokenCount": 40000 }`
or an explicit null for Automatic, and returns the full preferences snapshot. Missing values,
non-integers and counts outside 1–2,147,483,647 return 400 without saving. This preference applies
at the next compaction check in existing sessions. The effective count is capped by fixed model
headroom; retained history is capped at half that count and 20,000 tokens, excluding the summary.
An explicit count works for unknown model windows too; Automatic without a known window does
not trigger compaction. See [compaction design](compaction-design.md).

Settings → Model buckets sets the models for each tier and the delegation provider defaults
for **new** chats. `POST /api/preferences/model-bucket` replaces one tier's array using
`{ "bucket": "small", "models": [{ "connectionId": "…", "modelId": "…" }] }`.
`POST /api/preferences/subagent-providers` replaces the default provider selection. The composer’s
Subagents control changes only its own chat using `GET` and `POST /api/sessions/{sessionId}/subagent-providers`.
The two write routes for provider selection accept `{ "connectionIds": null }` for all saved
connections, an explicit connection-ID list, or `{ "connectionIds": [] }` to disable delegation.
Session routes return the chat's `connectionIds`; the defaults route returns the full preferences.

Before its first accepted message, a chat can override its own directory with
`POST /api/sessions/{sessionId}/working-directory` and `{ "workingDirectory": "C:\\path" }`.
The response contains the normalized `workingDirectory`; invalid paths return 400, missing
sessions 404, and locked/busy/fenced sessions 409. `canChangeWorkingDirectory` in the live
snapshot reports eligibility. Any accepted input locks the directory, even if provider setup
fails before transcript content appears; stored snapshots alone never grant this capability. The directory is not a sandbox: tools have the server
user's full access, without per-call approvals. Existing sessions with an empty frozen tool list
remain readable and tool-free. Optional `--git-bash` selects an
absolute Git for Windows Bash executable, independently of the working-directory preference;
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

On Windows, a ready server shows a notification-area icon (via
[H.NotifyIcon](https://github.com/HavenDV/H.NotifyIcon)); `--no-tray` disables it, and the
test launchers pass it. The tooltip shows the served address. Clicking the icon opens a menu:
**Open** launches the address in the default browser; **Stop Urd** requests the same graceful
shutdown as Ctrl+C. The icon is removed after shutdown finishes. Tray failures go to stderr
and never stop the server. On Windows 11 the icon is shown on the taskbar rather than in the
hidden-icons overflow, unless it has been hidden in Settings → Personalization → Taskbar.

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
| GET `/api/sessions/{sessionId}/message-submissions/{submissionId}` | 200 receipt or literal JSON `null`; 409 if an edit removed the input |
| POST `/api/sessions/{sessionId}/message-edits` | `MessageEditSubmission` → 200 `MessageEditReceipt` after Stop/settlement and committed replacement |
| GET `/api/sessions/{sessionId}/message-edits/{operationId}` | 200 committed edit receipt or literal JSON `null` |
| POST `/api/sessions/{sessionId}/executions/{executionId}/stop` | Empty JSON object `{}` → 200 `StopReceipt` for Requested or Settled |
| POST `/api/sessions/{sessionId}/continuations` | `ContinuationSubmission` → 200 `{sessionId, operationId, executionId}`; 409 when stale or unavailable |
| GET `/api/sessions/{sessionId}/continuations/{operationId}` | 200 retained receipt or literal JSON `null` |
| POST `/api/sessions/{sessionId}/executions/{executionId}/pending-inputs/{submissionId}/remove` | Empty JSON object `{}` → 200 boolean; false if already claimed, retired or unavailable |
| POST `/api/sessions/{sessionId}/compactions` | `ManualCompactionRequest` → 200 `CompactionReceipt`; 409 when admission is unavailable |
| GET `/api/sessions/{sessionId}/compactions/operations/{operationId}` | 200 receipt or literal JSON `null` |
| GET `/api/sessions/{sessionId}/compactions/{compactionId}` | `expectedHistoryRevision` → 200 `CompactionSummary`, or 404 |
| GET `/api/sessions/{sessionId}/snapshot` | 200 slim `SessionSnapshot` |
| GET `/api/sessions/{sessionId}/history` | Mode/revision and optional entry anchor → 200 bounded `TranscriptHistoryPage` with `toolLabels` |
| GET `/api/sessions/{sessionId}/markdown-images/{executionId}/{messageId}/{key}` | `historyRevision` and an image key from the assistant Markdown projection → 200 managed raster bytes, or 404 if unavailable |
| GET `/api/sessions/{sessionId}/prepass/{boundaryId}/{category}` | `historyRevision`, optional `candidateOffset` and `resultOffset` → 200 paged `PrepassDetails`, or 404 |
| GET `/api/sessions/{sessionId}/tool-calls/{requestId}/{callId}` | `expectedHistoryRevision`, `returned`, `full` → 200 `ToolCallTranscript`, or 404 |
| GET `/api/sessions/{sessionId}/not-sent?cursor=...` | Optional opaque cursor → 200 page of known-not-sent inputs |
| GET `/api/sessions/{sessionId}/content` | Segment-local cursor → 200 `MessageContentPage` |
| GET `/api/sessions/{sessionId}/positions` | `expectedHistoryRevision` → 200 `TranscriptPositionSummary` |
| GET `/api/sessions/{sessionId}/positions/locate` | `expectedHistoryRevision`, `fraction` (0–1) → 200 `TranscriptPositionWindow` |
| GET `/api/sessions/{sessionId}/positions/entries/{executionId}/{entryId}` | `expectedHistoryRevision`, `kind` → 200 positioned entry window |
| GET `/api/sessions/{sessionId}/positions/prompts` | `expectedHistoryRevision`, optional `afterRank` and `limit` (1–60) → 200 `TranscriptPromptPage` |
| GET `/api/sessions/{sessionId}/positions/prompts/{executionId}/{entryId}` | `expectedHistoryRevision` → 200 positioned user-prompt window |

[`SessionRoutes.cs`](../source/hosting/server/SessionRoutes.cs) owns the route inventory;
[`SessionClient.ts`](../source/ui/sessions/SessionClient.ts) supplies the browser query shapes.

Session snapshots include `canCompact`, a live hint that is true only while the session is
locally idle, with no active execution, durable unsettled run, admission fence or Stop request.
The compaction POST rechecks admission in Core and returns 409 when unavailable; exact retries
of an already accepted operation still return its retained receipt.

History and descriptor-carrying position windows attach `toolLabels`; full arguments,
results and storage excerpts are never included. Tool inspection requires explicit `returned`
and `full` booleans. Previews (`full=false`) cap each field at 65,536 characters and report
truncation; Copy requests full text. `inputTokens`/`outputTokens` estimate each full field
with o200k, scaled from its first 65,536 characters. Image references render without reading image bytes.

GET `/api/files/directories` lists immediate subfolders for an optional absolute `path`;
omitting it lists Windows drives (or the filesystem root elsewhere). An optional `filter`
matches folder names case-insensitively. The response is `{path, parentPath, directories,
truncated}`, with `{name, path}` directory entries, deterministic ordering and a 200-entry
limit. Filtering narrows truncated listings. Invalid queries return 400, missing folders 404,
inaccessible folders 403, and filesystem read failures 503. These bounded, cancellable reads
use the normal browser API gates; they neither reveal files nor change preferences.

POST `/api/files/reveal` accepts `{path}` with an existing absolute path and returns 204
on Windows. It starts Explorer directly without a shell; missing paths return 404.

### Browser event stream

`GET /api/events` opens a transient server-sent event stream. Its `connected` event provides
stream and process-boot IDs. Register a page's open and watched session IDs with
`POST /api/events/{streamId}` using `{open, watched}`. For each registration the server
subscribes before returning success and sends an initial `status` for every registered open
session, including its published `title` (or null). An unknown ID gets `unavailable` without
blocking other registrations. A watched session
that exists also gets an initial `change` hint. The browser waits for both the registration
response and that initial status before releasing a racing change, then reads
a bounded snapshot/history. This subscribe-before-read handshake avoids a gap between
initial state and live updates. Changes coalesce per session; if more arrive during a
snapshot/history read, the client performs a trailing read rather than overlapping reads.
Later `status` events update tabs when execution status or the published title changes, even
for unwatched tabs. `change` notices prompt bounded reads, and
`session-created` events are discovery hints. Heartbeats keep the stream active; an unacknowledged
registration independently times out and reconnects with the latest open/watched set. Events are
not durable history or message payloads. Reconnect performs saved-ID discovery again. The
existing local API marker and gates apply.

**Breaking vocabulary change:** there are no legacy route/member aliases.
A submission body is `{submissionId, expectedHistoryRevision, text, llmSelection}`.
A receipt is `{sessionId, submission, acceptance, refusalReason, executionId}`, retaining
that entire original submission, including its complete selected connection binding.
`acceptance` is `accepted` or `refused`; accepted receipts have an execution ID, refused
receipts have a refusal reason and null execution ID. Stop receipts and
`SessionExecutionResult` use `executionId`; snapshots use `unsettledExecutionId` and
`MessageRef` (including nested content cursors) uses `executionId`.
The vocabulary cleanup preserved saved IDs, revisions, SQL identifiers and decisions.
The current schema stores frozen selection, transcript metadata, request usage, known
send-attempt provenance and not-sent tracking. Incompatible pre-release schemas must be
reset, not migrated; [backend development](backend-development.md) identifies the version.
Session routes retain
their local admission gates, status mapping, no-store and read-cancellation rules. Historical
wire checks covered the renamed receipt, Stop, snapshot and cursor fields and rejected legacy
routes and required field names.

### Steering removal and Continue

Pending previews include `canRemove`; removal addresses the exact execution and submission.
It never stops the run, changes the accepted receipt or creates a not-sent warning. A false
reply means removal did not win; reread the snapshot instead of assuming it was removed.

`ContinuationSubmission` is `{operationId, expectedHistoryRevision, expectedLastExecutionId,
llmSelection}`. Admission atomically checks the settled tail and idle eligibility reported by
`canContinue`. The accepted execution ID equals the operation ID. Exact original retries return
the retained receipt, including after history edits or restart, without starting again; changed
reuse conflicts. A null lookup alone does not establish rejection. Continue adds no user row or
hidden user prompt. Failed/cancelled/dangling assistant-tail omission affects model context only,
not transcript history or prior tool effects. There is no automatic after-run dispatch.

### Earlier-message editing

`MessageEditSubmission` is `{operationId, message, text, llmSelection}`; `message` is the
exact user `MessageRef`, including its history revision. Core validates it before stopping
anything, fences competing admission, joins the current execution, and atomically replaces
the target and later history with a new accepted input. The receipt is
`{sessionId, operationId, historyRevision, executionId}`. It establishes replacement, not
successful completion of the new response. The selected model/reasoning settings are frozen
at Save, not inherited from the edited message.

The same operation and payload return the same receipt without starting another execution;
a changed payload under that ID conflicts. Receipt lookup may return null while Stop is
still settling, so null never proves rejection. Disconnect does not cancel admitted editing.
Before replacement commits, a restart leaves the old conversation (and interrupted work)
intact. After commit, normal execution recovery applies; it never repeats the edit or tools.
Shutdown fences and joins editing too, without admitting a replacement after shutdown begins.

Kept messages retain their IDs under a new history revision. Removed text, response/tool
bodies, native provider context and obsolete summaries are erased from the logical store;
compact command identities/hashes and usage remain. Removed Send receipts return 409 rather
than exposing deleted text or allowing an old Send to recreate it. History replacement does
not undo external tool effects. There is no branch archive or undo.

The current schema is **31**, including the compaction-trigger preference. Existing schemas require explicit database
recreation under the pre-release policy. Never reset an active profile as part of verification
or automatically activate this build against one.

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
History entries include tool names and execution status. Inspect arguments/results on demand
at `/api/sessions/{sessionId}/tool-calls/{requestId}/{callId}` with explicit `returned` and
`full` flags. Previews retain at most 65,536 characters per field at the storage read and only
measure the remaining model-facing text, without loading inspection-only details, and report truncation. `projectionFinal` means
the model-facing image projection is fixed for this history revision. Copy can reuse a
complete preview only when that flag is true and the tool payload has finished.

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
