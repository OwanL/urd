# Backend development

The current [test scope and hard size budget](testing.md) describes retained coverage;
checkpoint totals are historical, not the current suite size.

Core remains an ASP.NET-free .NET library. The [local Server](server-development.md)
composes it with the shared SQLite store and provider manager. The current flow includes
saved-primary-ID discovery, durable Send decisions, safe-boundary steering, addressed
Stop, bounded snapshot/history/content reads, known-not-sent discovery and graceful
shutdown. The browser workspace is implemented and served as static assets by the Server.

`UrdStore` owns the shared database, currently schema 12. It records whether a request
started sending and pages inputs known not to have been sent, so users can retrieve
and explicitly resend them. Snapshots stay slim; transcript metadata and
content use separate bounded reads, and reported usage is persisted per request. Provider
settings are available in the browser workspace, and production sessions borrow clients from
`ProviderManager`. See [provider management](provider-management.md) for connection and
credential behavior.

Before the supported public data baseline, schema changes replace the schema rather than
migrating it. A fresh/version-0 database creates all current tables and indexes in one
transaction; version 12 reopens normally. Other versions fail with reset instructions:
stop Urd, delete `urd.sqlite`, `urd.sqlite-wal` and `urd.sqlite-shm`, then restart.
Urd never deletes these files automatically. Requests have only known not-attempted (1)
or attempted (2) provenance; transcript user entries require request links and receipts
require their original model selection. Empty sessions still have no selection.
Storage uses `response_id`/`response_final` and request-global chunk indexes.

## Naming and structure cleanup

Core and HTTP now use **user message submission / acceptance / session execution**:
`UserMessageSubmission(SubmissionId, ExpectedHistoryRevision, Text, LlmSelection?)` and
`UserMessageReceipt(SessionId, Submission, Acceptance, RefusalReason, ExecutionId)`;
`MessageAcceptance` and `MessageRefusalReason` retain the existing enum values/meanings.
`GetUserMessageReceiptAsync(submissionId)` reads the retained receipt or null.
Execution identities are `ExecutionId`, including `StopReceipt`, `SessionExecutionResult`
and `MessageRef`; snapshots use `UnsettledExecutionId`.
This is a breaking vocabulary change, with **no legacy aliases**. At that checkpoint,
saved IDs, revisions, SQL identifiers/schema 5, decisions and exact-retry semantics were
unchanged. [Server](server-development.md#session-http-contract) records the wire contract.
Historical verification included raw HTTP assertions for the renamed JSON members and
rejection of legacy routes/required field names, alongside retry, reopen, migration, Stop
and bounded-read checks.
`SessionSnapshot` is now the single snapshot shape. `SnapshotReader.ReadStored` supplies
stored facts with a conservative admission fence; `SessionBase.ReadSnapshotAsync` applies
the same live admission/Stop rules under its existing lock before returning an immutable
view. The separate stored snapshot and duplicate field mapping are removed. The private
settlement bookkeeping pair is a named tuple, and shared tool JSON validation lives in
`StorageText`, rather than making LLM response storage depend on tool storage. Other storage
classes remain separate by responsibility; no lifecycle or schema redesign accompanies this cleanup.

## Markdown rendering

`source/backend/core/markdown-rendering/` owns the detached Markdown rendering
pipeline: `MarkdownParser` produces a `MarkdownRenderIndex`, `MarkdownPageReader`
serves bounded forward/backward pages and manifests, and `MessageMarkdownScheduler`
coordinates source capture, queued parsing and two BelowNormal lanes (one large, one
small). `MessageMarkdownLease` keeps a generation readable during handoff; pinned
generations and leases still count against explicit cache budgets. Explicit limits
remain required. The pipeline is not wired into session ownership or HTTP routes.

## Checkpoint scope

- `SessionManager(store, createChatClient, IReadOnlyList<AIFunction>? tools = null)` passes the
  borrowed store and client factory to canonical session handles. It snapshots the optional tool
  list, not the functions themselves; the host owns the store, factory and functions. The factory
  receives the frozen selected connection binding. Constructors start no work.
- `PrimarySession.SubmitUserMessageAsync(submission)` returns a durable acceptance/refusal receipt,
  not an LLM answer. Acceptance and execution identity commit together. Exact retries return
  the original decision; neither lookup nor retained acceptance starts work. Refusal fences delayed originals.
  Changed-submission reuse throws `UserMessageConflictException` with its `SubmissionId`, so HTTP can distinguish
  conflicts from shutdown or uncertain failure without parsing exception messages.
- `SessionBase` owns admission, database writes and lifecycle coordination under its existing guard.
  Each execution has one `SessionExecution` and one driver, with sequential request phases outside the guard.
  Execution owns LLM streams, tool invocation and the execution's Stop source; a request-local
  `LlmResponseWriter` owns bounded UTF-8 batches and their flush deadline.
- Public `SessionBase.RequestStopAsync(Guid executionId)` returns `StopReceipt(ExecutionId, Status, Outcome)`:
  `Requested` acknowledges committed Stop intent/steering retirement, not completed cancellation;
  `Settled` carries the saved outcome; `NotFound` includes wrong-session IDs; `Unavailable` means
  an unresolved saved execution is not locally owned. Empty IDs are invalid. Retries report current status,
  so Requested can advance to Settled; stale Stop cannot affect a replacement.
- Stop initiates cached asynchronous cancellation under the guard even if its save fails.
  The driver, not the public/HTTP caller, joins callbacks outside it before settlement.
  Replacement requires LLM/tool cleanup, successful callbacks and saved settlement.
  Callback failures or an unexpected missing claim/settlement fence the execution; they do not retry it.
  Failed Stop persistence fences admission and fails the request without a false durable acknowledgement.
  Local `IsStopping` also blocks output, successful request completion, claims and tool starts;
  actual tool results may still be saved. Snapshots merge local Stop intent for owned unsettled work
  with stored Stop state; this observation is not a durable acknowledgement.
- A distinct busy Send defaults to durable steering in the live owned execution, within a limit of
  eight nonterminal input deliveries. Pending inputs join the next request as separate user
  messages in admission order, after any tool results. A claimed request is never mutated.
  Pending steering also continues after a successful no-tool response at the safe boundary.
  Claiming the next request or settling happens under the same guard, so racing admission cannot
  strand accepted input. Capacity, revision and Stopping refusals remain durable decisions.
- Configured tool batches are authorized only after successful response completion and provider cleanup;
  calls execute sequentially, at most 16 per response. The old 16 KiB tool JSON write caps
  are removed; bounded reads protect display paths instead.
  Ordinary tool errors become feedback and remaining calls/LLM continuation proceed, including
  failures inside an opaque `AIFunction`'s binding/marshalling. Such errors do not guarantee no effects.
  Core-owned result encoding, persistence or cleanup faults fence uncertain work instead of
  being converted into ordinary tool errors. Reported usage is accumulated and persisted with its
  request completion when supplied; missing usage remains unknown.
- Stop retires pending deliveries without rewriting Accepted receipts or automatically carrying
  them into another execution. No explicit after-execution queue is implemented.
- Public snapshot, history, not-sent and content reads expose committed state without starting
  work; cancelled reads do not cancel execution. Reopened orphan executions and live persistence
  faults are reported as fenced.
- `SessionManager.ShutdownSessionsAsync(waitCancellation)` fences creation/lookup/listing and input on every
  held handle, then stops and drains owned executions through cleanup and saved settlement. Concurrent/repeated
  calls join one retained outcome; cancellation abandons only the caller's wait, not shutdown. A failed
  Stop write still triggers local cancellation and draining, and the shutdown failure remains visible.
- The caller gates/drains external operations before disposing the borrowed store/client. Handle reads
  and retained receipts remain available during shutdown; new inputs receive durable Stopping refusals.
  Shutdown does not close saved conversations, recover orphan executions or shut down the host itself.
  Cancellation callbacks may reenter reads/admission, but must not await shutdown or their own settlement.
- Settlement waiting remains internal; public snapshots observe stopping and saved settlement.
  Unowned orphan Stop does not mutate/recover/replay the execution or fabricate settlement.
  Content reads expose user/assistant text; history pages also expose bounded tool-entry status
  and names, but not tool arguments or results. An admission receipt is not completion.
- Normal terminal close/reopen is tested without replay. Nonterminal rows survive reopening
  and block new admission; startup interruption/recovery is **not** implemented.

Earlier Stop and steering checkpoints used real SQLite and HTTP with held callbacks/cleanup,
uncooperative fake tools, immutable receipts, stale/wrong-session Stop isolation, unowned
orphans, lost acknowledgements, rollback, local cancellation after failed saves, and safe-boundary
continuation. Those are historical results, not current suite totals or coverage claims.
No real-process or subagent termination qualification is implied. Review
`sessions/SessionStopTests.cs`, `storage/StopStorageTests.cs` and
`hosting/SessionStopRoutesTests.cs` under `source/tests/Urd.Backend.Tests/` for retained
representative cases. Full bounded-descendant ownership/join requirements remain in
[architecture](ARCHITECTURE.md) and [session design](session-design.md). Functions must honor
cancellation and join their owned work; Stop neither undoes effects nor forcibly kills
arbitrary in-process functions.

### Earlier checkpoints

Earlier feature checkpoints covered concurrent clients, exact retries/reopen, transactional
rollback, Unicode-safe streaming, cleanup and persistence failures, bounded content/listing
reads, Stop/shutdown races, steering and real HTTP behavior. Their test totals and several
intermediate feature descriptions no longer describe the current suite. See
[the current test scope](testing.md) for retained coverage owners and deliberate reductions.

## Saved-primary-ID discovery

`SessionManager.ListPrimaryIdsAsync(string? cursor = null, CancellationToken cancellationToken = default)`
returns `SessionIdPage(IReadOnlyList<Guid> SessionIds, string? NextCursor)`: at most 60 IDs,
newest-created first, including empty chats and retained unsettled chats. It takes the existing
manager guard, rejects shutdown and reads storage without creating/caching handles, reading
history, starting execution or changing persisted state. Cancellation can abandon the guard
wait or precede the synchronous read; it does not promise SQLite query interruption.

Core owns the opaque string cursor (positive Int64 ordinal encoded as invariant decimal).
Clients copy it unchanged. One indexed query reads at most 61 rows; a lookahead yields
`NextCursor` from the last returned row, otherwise null. Continuations use `ordinal < cursor`,
not a boundary-ID lookup or offset. Boundary deletion does not invalidate paging; new creations
stay ahead of continuation pages. Each query observes committed state, not a frozen multi-page
snapshot; refresh the first page to discover new chats. No total count or configurable limit.

`session_creation` stores an AUTOINCREMENT ordinal and unique session-ID foreign key
with cascading metadata cleanup. Identity and ordering metadata insert atomically in the existing create transaction; handles are
returned/tracked only after commit. Ordinals are not reused after deletion; gaps are harmless.
The cascade does not add a public delete/close operation; `SessionRecord` is unchanged.

GET `/api/sessions?cursor=...` exposes the page through the existing local gates/no-store/no-body
GET rule; see [Server](server-development.md). The API returns IDs only: it adds no summaries,
titles or last-used tracking. The browser uses IDs to reconcile saved chats, while transcript
history and content have separate bounded reads. An ID proves neither usability nor stopped work.
Listing cannot associate a returned ID with a lost create acknowledgement or justify automatic
create retry.

`SessionListingStorageTests` covers atomic creation, schema upgrade/rollback, preserved evidence,
paging across inserts/reopen/deleted boundaries and ordinal nonreuse. Session and HTTP coverage
checks no handle hydration/replay, cancellation, gates and storage failures. These qualify ID
discovery, not activity tracking or a complete summary API.

## Read contract and incremental review order

1. [SessionSnapshot.cs](../source/backend/core/sessions/SessionSnapshot.cs) and
   [Messages.cs](../source/backend/core/sessions/Messages.cs) — detached records; shared
   `SessionExecutionResult`/`ExecutionOutcome` in [SessionExecutionResult.cs](../source/backend/core/execution/SessionExecutionResult.cs)
   are public, not duplicated. `ExecutionOutcome` describes an LLM request attempt, a tool invocation
   or a whole session execution; a successful request/tool does not imply settled execution.
2. [SessionBase.cs](../source/backend/core/sessions/SessionBase.cs) — public snapshot, history,
   not-sent and content reads take the existing guard; snapshots merge live admission fencing/local
   Stop intent with one committed view.
3. [SnapshotReader.cs](../source/backend/core/storage/SnapshotReader.cs),
   [TranscriptHistoryReader.cs](../source/backend/core/storage/TranscriptHistoryReader.cs) and
   [MessageContentReader.cs](../source/backend/core/storage/MessageContentReader.cs) — separate
   bounded reads in short transactions. [DatabaseSchema.cs](../source/backend/core/storage/DatabaseSchema.cs)
   is at schema version 10. Earlier versions added creation order, provider state/native context,
   frozen selection, transcript metadata and request usage; version 10 adds request-attempt and
   not-sent facts while preserving transcript ordering.
4. [MessageContentTests.cs](../source/tests/Urd.Backend.Tests/storage/MessageContentTests.cs) and
   [SessionSnapshotStorageTests.cs](../source/tests/Urd.Backend.Tests/storage/SessionSnapshotStorageTests.cs).

Snapshots are slim committed facts: identity/revision and admission/Stop/settlement state,
latest selection, a short first-user preview, and up to eight pending-input previews. They do not
contain a recent transcript window. The `/history` read returns at most 60 entry descriptors
with text cursors or tool status; `/content` fetches message bytes separately. The browser shows
the latest history page and loads visible text on demand; older-page navigation is not exposed
in the UI yet.

Call `ReadMessageContentAsync` with `new MessageContentCursor(messageRef, 0, 0)`.
Each page returns at most 16 KiB of original UTF-8 and examines at most 16 immutable segments.
Follow `Next` while `HasMore`; a false value means the current committed tail, not necessarily
final output. Segment-local byte offsets preserve Unicode and embedded NUL; stale revisions
throw `HistoryRevisionConflictException`. `IsClosed` concerns further bytes;
`FinalOutputCommitted` is a saved final marker. Neither means successful execution settlement or
finished cleanup. The browser observes bounded server events, then refreshes snapshot/history
metadata and reads message content on demand.

## Review order

Paths below are relative to the repository. Classes have their own files; small data records and enums
share a file per concept. Read them alongside their owners.

1. [SessionManager.cs](../source/backend/core/sessions/SessionManager.cs) — canonical handles,
   borrowed dependencies, ID-only discovery, the session-shutdown boundary and `SessionRecord`
   identity used to create/load handles; [SessionIdPage.cs](../source/backend/core/sessions/SessionIdPage.cs)
   is the detached discovery page.
2. [SessionBase.cs](../source/backend/core/sessions/SessionBase.cs) and
   [PrimarySession.cs](../source/backend/core/sessions/PrimarySession.cs) — admission/driver ownership;
   [UserMessageSubmission.cs](../source/backend/core/sessions/UserMessageSubmission.cs) holds the submission, receipt and enums;
   [Stop.cs](../source/backend/core/sessions/Stop.cs) holds the public Stop receipt/status;
   [AsyncLock.cs](../source/backend/core/sessions/AsyncLock.cs) is the shared async lock.
3. [SessionExecution.cs](../source/backend/core/execution/SessionExecution.cs) — sequential request/tool
   phases, response streaming, cancellation and cleanup;
   [LlmResponseAccumulator.cs](../source/backend/core/execution/LlmResponseAccumulator.cs) — complete
   fake-tool batch validation and bounded JSON freezing;
   [LlmTools.cs](../source/backend/core/execution/LlmTools.cs) — call/result records; then
   [LlmResponseWriter.cs](../source/backend/core/execution/LlmResponseWriter.cs) — request-local
   Unicode-safe chunking and flush timing. [LlmRequest.cs](../source/backend/core/execution/LlmRequest.cs) holds
   `LlmRequest`, `LlmResponseChunk` and `LlmRequestResult`; [SessionExecutionResult.cs](../source/backend/core/execution/SessionExecutionResult.cs)
   holds the saved whole-execution `SessionExecutionResult` and `ExecutionOutcome`. Their comments identify
   producer/consumer roles and lifecycle guarantees.
4. [UrdStore.cs](../source/backend/core/storage/UrdStore.cs) — opens the database and exposes
   focused parts: [Database.cs](../source/backend/core/storage/Database.cs) (connection, lock,
   transactions), [DatabaseSchema.cs](../source/backend/core/storage/DatabaseSchema.cs) (current schema creation),
   [SessionStorage.cs](../source/backend/core/storage/SessionStorage.cs),
   [UserMessageStorage.cs](../source/backend/core/storage/UserMessageStorage.cs) (receipts),
   [SessionExecutionStorage.cs](../source/backend/core/storage/SessionExecutionStorage.cs) and
   [LlmResponseStorage.cs](../source/backend/core/storage/LlmResponseStorage.cs) — atomic execution/request/response and delivery facts;
   these are exposed internally as `UserMessages`, `Executions` and `LlmResponses`.
   [ToolStorage.cs](../source/backend/core/storage/ToolStorage.cs) — dispatch claims and retained results;
   [LlmContextReader.cs](../source/backend/core/storage/LlmContextReader.cs) — claimed submissions and
   ordered assistant/tool context, distinct from the public transcript metadata/content projection.
5. [SessionConcurrencyTests.cs](../source/tests/Urd.Backend.Tests/sessions/SessionConcurrencyTests.cs),
   [SessionShutdownTests.cs](../source/tests/Urd.Backend.Tests/sessions/SessionShutdownTests.cs),
   [LlmResponseWriterTests.cs](../source/tests/Urd.Backend.Tests/execution/LlmResponseWriterTests.cs) and
   [SessionExecutionTests.cs](../source/tests/Urd.Backend.Tests/execution/SessionExecutionTests.cs) — whole Core flow,
   concurrent retries, cancelled waiters and lifecycle failures.
6. [UserMessageReceiptTests.cs](../source/tests/Urd.Backend.Tests/storage/UserMessageReceiptTests.cs),
   [ExecutionStorageTests.cs](../source/tests/Urd.Backend.Tests/storage/ExecutionStorageTests.cs) and
   [SessionIdentityTests.cs](../source/tests/Urd.Backend.Tests/sessions/SessionIdentityTests.cs) — storage/registry invariants;
   [TemporaryDatabase.cs](../source/tests/Urd.Backend.Tests/support/TemporaryDatabase.cs) supplies isolated test data.
   `support/` also holds the scripted LLM client, response scripts and execution fixture;
   test folders mirror the source folders.
7. [SteeringFlowTests.cs](../source/tests/Urd.Backend.Tests/sessions/SteeringFlowTests.cs),
   [ToolExecutionTests.cs](../source/tests/Urd.Backend.Tests/execution/ToolExecutionTests.cs) and
   [ToolStorageTests.cs](../source/tests/Urd.Backend.Tests/storage/ToolStorageTests.cs) — representative
   safe-boundary steering, cleanup before effects, invalid batches and durable claim/result failure.
   See [test scope](testing.md) for current coverage owners and deliberate reductions.
   `support/StoredDeliveries.cs` and `support/StoredTools.cs` supply the corresponding durable evidence.

## Prerequisite and commands

Install the exact SDK declared in `source/global.json` without changing that pin
just to match another machine. SDK **10.0.403** is a stable .NET 10 LTS servicing
release; Microsoft's channel metadata lists active support through 2028-11-14.
It supports this slice's `net10.0` target. No bootstrap/install script is provided.

Run from `source/`, because SDK selection follows the working directory:

```powershell
Set-Location 'C:\dev\repos\urd\source' # or your own copy's source directory
$dotnet = 'C:\Program Files\dotnet\dotnet.exe' # adjust for your SDK installation
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1' # this terminal/child processes only
$env:MSBUILDDISABLENODEREUSE = '1'
& $dotnet --version # must print the global.json pin
& $dotnet restore Urd.slnx --locked-mode --disable-parallel -m:1 -nodeReuse:false
& $dotnet test Urd.slnx --no-restore --configuration Release --disable-build-servers -m:1 -p:UseSharedCompilation=false
```

Normal restore is locked and must not change the committed `packages.lock.json`
files. A mismatch fails instead of silently upgrading. Restore needs public NuGet
access or already cached packages; it does not require personal credentials.
Test also builds Core, Server and tests with nullable checking and the code-quality gate below.
The command avoids persistent build/compiler servers and limits build parallelism.

`Microsoft.Data.Sqlite` **10.0.14** follows the stable .NET 10 provider line and
includes its SQLite native bundle. `Microsoft.Extensions.AI.Abstractions` and
`Microsoft.Extensions.AI.OpenAI` **10.11.0** provide the request abstraction and adapter;
OpenAI **2.14.0**, Duende OIDC **7.1.1**, Duende IdentityModel **8.1.0**, and
Microsoft.IdentityModel.JsonWebTokens **8.23.0** are pinned in `Urd.Core.csproj`.
Tests use xUnit **2.9.3**, runner **4.0.1**, and Microsoft.NET.Test.Sdk **18.11.0**.
SonarAnalyzer.CSharp **10.35.0.4138** is shared through
`source/Directory.Build.props` with `PrivateAssets="all"`; it is a build-time dependency,
not part of the application API/runtime dependency graph. Versions live at these package
references; lockfiles retain the transitive graph and content hashes.

For an intentional reviewed dependency update only, edit the project reference and
regenerate the project locks from `source/` with:

```powershell
& $dotnet restore Urd.slnx --force-evaluate -p:RestoreLockedMode=false --disable-parallel -m:1 -nodeReuse:false
& $dotnet restore Urd.slnx --locked-mode --disable-parallel -m:1 -nodeReuse:false
```

Review the lock changes and run tests before committing. Do not disable locked mode
for ordinary builds. SDK maintenance is a separate explicit `global.json` edit.

## Code-quality gate

Ordinary `dotnet build` and `dotnet test` commands enforce the gate in Core, Server and
their tests, in Debug and Release. No separate lint command, IDE extension, Sonar server
or cloud upload is required. `dotnet test --no-build` deliberately skips compilation
and therefore is not a quality-gate check.

- `source/Directory.Build.props` selects the .NET 10 recommended analyzer rules,
  enables build-time code-style analysis, and loads the pinned Sonar analyzer.
  Compiler/analyzer warnings are errors: a finding stops compilation before tests run.
- `.editorconfig` promotes unused private members, unread private members, dead
  assignments and unused non-public parameters to warnings. Other IDE style rules
  retain their default severities; formatting preferences are not blanket build errors.
- Sonar's enabled-by-default rules catch additional bug and smell patterns. Disabled
  rules and suggestions do not become errors merely because warnings-as-errors is on.
  This is a concrete rule set, not a promise to detect every smell or prove correctness.

Fix findings rather than disabling analysis or lowering severity to make a build pass.
An intentional exception must name the specific rule, explain the actual invariant or
false positive, and cover the smallest practical scope. There is no blanket existing-code
baseline. Current local exceptions preserve private execution signals, deferred validation
of captured input arguments, the async lock's managed-only semaphore use, and deliberately
failing provider cleanup in a test. CA1861 alone is disabled for test sources so expected
arrays stay beside their assertions rather than becoming shared fixture fields; production
keeps that rule. Revisit exceptions when their surrounding behavior changes.

Analyzer upgrades are reviewed dependency changes: update the shared reference and project
locks, triage new diagnostics, then run tests. Verify the gate itself with deliberately bad
code in an isolated copy: builds and tests must exit unsuccessfully with the expected rule
IDs, and clean code must pass again after removing the probe. Passing tests or analyzers
still does not replace review of ownership, concurrency, SQL or error handling.

## Test effectiveness

Prefer improving an existing behavioral case over adding overlapping tests or fixture
machinery. Assert observable outcomes and durable state, not private helper calls or
incidental execution order. Assertions on durable state read through the shared `support/`
readers (`StoredExecutions`, `StoredSubmissions`, `StoredLlmResponses`, `StoredSessions`, `StoredDeliveries`, `StoredTools`),
not ad-hoc SQL, so schema evolution
touches one reader instead of every test. Raw `Scalar(...)` observation inside a `*Tests.cs`
file needs a same-line `// raw-sql:` justification, which the pre-commit hook enforces on
staged files; current-schema constraint checks and fixture-owned witness tables are the expected
exceptions. Forging state the API would never produce (stale revisions, corrupt bytes,
rollback witnesses) stays in arrangement code, shared where repeated (for example
`TemporaryDatabase.ForceHistoryRevision`). Coverage helps find untested paths; it is not a quality score
or a percentage target. For critical invariants, use small semantic mutations in an
isolated copy and verify that tests—not compiler/analyzer errors—reject the broken
behavior. Keep waits bounded and cleanup safe; teardown must not mask the original
assertion failure or dispose resources while owned work may still be running.

## Local secret checks

From the repository root, enable the staged-change commit check on each new Windows x64
checkout:

```powershell
powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File .githooks/install.ps1
```

The installer downloads pinned Gitleaks **8.30.1**, verifies its SHA256, stores the binary
and license under ignored `.tools/gitleaks/`, and sets only this checkout's
`core.hooksPath` to `.githooks`. It refuses to replace another hook setup. No machine-wide
installation or CI is involved. Other platforms need their native Gitleaks binary at
`.tools/gitleaks/gitleaks` and the same local hook setting; the installer is Windows x64 only.

Commits scan **staged changes**, not the working copy or full history, using the standard
rules in `.gitleaks.toml`. Findings are redacted; findings, scanner errors and a missing
scanner block the commit. Commits do not download tools or run builds/tests. The hook also rejects staged `*Tests.cs`
changes whose raw SQL observation lacks a `// raw-sql:` justification (see Test
effectiveness). Re-run the
installer if the managed scanner is missing; review the version and checksum together
when upgrading it. Investigate findings rather than bypassing the check or adding a
blanket allowlist. Hooks are a local guardrail, not access control or proof that no secret
exists. Existing history needs a separate scan.

Local `.env` files and SQLite databases/sidecars are ignored; `.env.example` and
`.env.template` remain trackable and must contain placeholders, never real credentials.
Git attributes normalize text to LF, preserve detected binary files, and check out
`.bat`/`.cmd` files with CRLF. Adding this policy does not require mass-renormalizing files.

## Outputs and data

Normal generated `bin/`, `obj/` and test results stay inside the repository and are
ignored. NuGet uses its standard package cache. Tests own disposable temporary
SQLite directories; they must not open personal sessions, credentials or copy `data/`.
Build/test never resets application data. No clean, package or activation workflow
is implemented by this slice. During coordinated agent work, restore/build/test
must acquire, start and release the exact exclusive resource `urd:dotnet`.
