# Backend development

The current Core checkpoint includes session identity, durable user-Send decisions,
fake-text/tool execution with default durable steering, bounded snapshot/content reads
through real SQLite, graceful session shutdown, public addressed Stop and owner-approved
saved-primary-ID discovery. The current listing increment passed locked restore and
**295/295 Release tests**, without warnings, plus independent review.
Core remains an ASP.NET-free .NET library. The [local Server](server-development.md)
now exposes seven chat HTTP operations over it; UI is not implemented yet.

## Breaking API vocabulary — phase 1

Core and HTTP now use **user message submission / acceptance / session execution**:
`UserMessageSubmission(SubmissionId, ExpectedHistoryRevision, Text)` and
`UserMessageReceipt(SessionId, Submission, Acceptance, RefusalReason, ExecutionId)`;
`MessageAcceptance` and `MessageRefusalReason` retain the existing enum values/meanings.
`GetUserMessageReceiptAsync(submissionId)` reads the retained receipt or null.
Execution identities are `ExecutionId`, including `StopReceipt`, `SessionExecutionResult`
and `MessageRef`; snapshots use `UnsettledExecutionId`.
This is a breaking vocabulary change, with **no legacy aliases**. Saved IDs, revisions,
SQL identifiers/schema 5, decisions and exact-retry semantics are unchanged.
[Server](server-development.md#session-http-contract) records the renamed wire contract.
This naming pass passed locked restore and **296/296 Release tests** without warnings;
independent acceptance review found no blockers. Raw HTTP assertions verify the renamed
JSON members and reject legacy routes/required field names, alongside existing retry,
reopen, migration, Stop and bounded-read checks. Counts below describe earlier checkpoints.
Phase-2 structural cleanup is not included here: `StoredSessionSnapshot`, the private
settlement record and the existing JSON helper remain.

## Checkpoint scope

- `SessionManager(store, client, IReadOnlyList<AIFunction>? tools = null)` passes borrowed
  dependencies to canonical session handles. It snapshots the optional tool list, not the
  functions themselves; the caller owns store, client and functions. Constructors start no work.
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
- Fake-tool batches are authorized only after successful response completion and provider cleanup;
  calls execute sequentially, at most 16 per response, with 16 KiB JSON arguments/results per call.
  Ordinary tool errors become feedback and remaining calls/LLM continuation proceed, including
  failures inside an opaque `AIFunction`'s binding/marshalling. Such errors do not guarantee no effects.
  Core-owned result encoding, persistence or cleanup faults fence uncertain work instead of
  being converted into ordinary tool errors.
- Stop retires pending deliveries without rewriting Accepted receipts or automatically carrying
  them into another execution. No explicit after-execution queue is implemented.
- Public snapshot/content reads expose committed state without starting work; cancelled reads
  do not cancel execution. Reopened orphan executions and live persistence faults are reported as fenced.
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
  Public reads expose user/assistant text only; tool-inspection UI/API, push subscriptions,
  real tools/providers and provider-native
  context fidelity remain deferred. An admission receipt is not completion.
- Normal terminal close/reopen is tested without replay. Nonterminal rows survive reopening
  and block new admission; startup interruption/recovery is **not** implemented.

Public Stop passed independent production/test review and the full **281/281** Release suite.
Real SQLite and HTTP checks cover prompt acknowledgement during held callbacks/cleanup,
uncooperative fake tools, immutable steering receipts, stale/wrong-session Stop isolation,
unowned orphans, lost acknowledgements, rollback and local cancellation after a failed save.
In an isolated copy, tests rejected deliberate removal of local Stop write guards and the
callback join; all four restored controls passed. No real-process or subagent termination
qualification is implied. Review `sessions/SessionStopTests.cs`,
`storage/StopStorageTests.cs` and `hosting/SessionStopRoutesTests.cs` under
`source/tests/Urd.Backend.Tests/`; shared `support/HeldDisposal.cs` holds provider cleanup
without duplicating the existing content-read test machinery.

The previous steering/fake-tool checkpoint passed locked restore and the full Release
suite: **259/259** tests, with no warnings.
Coverage adds durable busy admission and bounds,
ordered tool-result/steering continuation (including no-tool boundaries), claim-or-settle races,
Stop/shutdown retirement, ordinary tool errors versus uncertain-effect fencing, transactional
schema-4 migration/rollback and real HTTP busy Sends through the unchanged routes.
In an isolated copy, focused tests rejected deliberate regressions that dropped no-tool
continuation or blocked continuation on a saved tool error; both restored controls passed.
These are fake-client/tool checks, not real-tool/provider or UI qualification.
Public Stop adds no schema, framework, UI, real tools or child runtime. Full bounded-descendant
ownership/join requirements remain in [architecture](ARCHITECTURE.md) and
[session design](SESSION-DESIGN.md); separately opened independent worker chats continue.
Functions must honor cancellation and join their owned work; Stop neither undoes effects nor
forcibly kills arbitrary in-process functions. No subagent-runtime qualification is claimed.

### Historical checkpoint evidence

The following counts and measurements describe earlier slices, not the current total.
At Core checkpoint `06c1c3d`, locked restore and the Release suite passed **35/35** tests,
with no build warnings/errors.
Coverage includes shared-client concurrent chats, exact retry/reopen, transactional rollback,
UTF-8 batching, positive terminal evidence, held cleanup and persistence-failure fencing.
The approved read increment passed **61/61** Release tests after locked restore, with no
build warnings/errors. Coverage adds scalar-safe full-content paging, committed tail following,
final output during held cleanup, reopened/faulted fences, exact recent-window boundaries,
indexed selection over 9,000 mixed-history runs, and atomic version-2-to-3 migration/rollback.
The subsequent naming/decision-readability pass passed **66/66** Release tests after
locked restore, including five cases covering overlapping admission-refusal conditions;
that pass changed no behavior or schema.
The owner-approved execution redesign passes **73/73** Release tests after locked restore,
with no build warnings/errors. Added coverage exercises invalid/incomplete Unicode, empty and
exact-size final chunks, reentrant/held cancellation callbacks, repeated and stale Stops,
and callback-failure fencing. The redesign changes internal lifetime coordination, not schema.
The readability review passes **86/86** Release tests after locked restore. Added cases cover
unsupported content, updates after Stop, trailing usage, repeated high surrogates and immutable
completion retries after settlement. SQL, transactions and lifecycle ownership remain unchanged;
clearer diagnostic wording from the earlier refactor is retained for new failures.
The follow-up smell pass passes **90/90** Release tests, adding coverage for awaited chunk saves,
write-failure ordering, cancelled lock waiters and stored preview identities. It removes per-character
async work, avoidable allocations and repeated revision queries within a snapshot transaction.
The response-batching optimization passes **101/101** Release tests. It counts strict UTF-8
and appends bounded spans, retaining scalar handling for single units and Unicode/chunk boundaries.
An additional temporary differential check matched the prior writer on 541 fragmented valid and
malformed inputs. A local .NET 10 Release microbenchmark (64 warmups, nine alternating samples of
64 responses, prebuilt text and synchronous no-I/O saves) measured roughly **4.5–5.6×** faster
batching for 32-character updates and **9–11×** for oversized ASCII/mixed text. Single-character
updates were roughly unchanged; measured allocations did not increase. These are isolated writer
measurements with default runtime settings, not end-to-end provider or storage speedups.
The graceful session-shutdown slice passes **112/112** Release tests after locked restore,
with no build warnings/errors. Eight added cases cover empty/completed/active managers,
all-handle fencing before reentrant callbacks, held cleanup/callbacks, cancelled/repeated waits,
failed Stop writes (including failures in multiple sessions), already-faulted settlement,
borrowed resources and reopen without recovery or replay. In an isolated copy, the new tests
also rejected deliberate regressions that dropped concurrent-session failures or skipped a
failed run's drain; the restored copy passed again. No schema or dependency changes.
These are fake-client/Core checks, not browser polling, load or real-provider qualification.
This is a Core review checkpoint, not the browser-to-Core milestone or a complete hosting API.
The subsequent health-only Server slice passes **184/184** Release tests after locked restore;
its 72 added cases and narrower hosting evidence are recorded in [server development](server-development.md).
The five-operation HTTP slice passes **210/210** Release tests after locked restore, adding 26
hosting cases and a typed input-conflict error without changing Core policy, schema or dependencies.
See that same record for the real HTTP flow, controlled active-work shutdown evidence and limits.

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

Schema 5 adds `session_creation`: an AUTOINCREMENT ordinal and unique session-ID foreign key
with cascading metadata cleanup. Migration follows existing upgrades in their transaction,
preserving identities, revisions, history and execution/receipt/tool evidence without rebuilding
the root sessions table or disabling foreign keys. Legacy rows have no recorded creation
chronology: ascending `session_id COLLATE BINARY` assigns a fixed fallback, returned in descending
binary-ID order, **not recovered chronology**. Subsequent creations sort ahead of that legacy band.
Identity and ordering metadata insert atomically in the existing create transaction; handles are
returned/tracked only after commit. Ordinals are not reused after deletion; gaps are harmless.
The cascade does not add a public delete/close operation; `SessionRecord` is unchanged.

GET `/api/sessions?cursor=...` exposes the page through the existing local gates/no-store/no-body
GET rule; see [Server](server-development.md). IDs are discovery only: existing explicit
snapshot/content/receipt reads supply details. No summaries, titles, full-history navigation,
last-used tracking, UI or automatic recovery is added. An ID proves neither usability nor stopped
work. Listing can rediscover saved IDs, but cannot associate one with a lost create acknowledgement
or justify automatic create retry.

Locked restore and **295/295 Release tests** pass without warnings; independent review found
no blockers. `SessionListingStorageTests` covers atomic creation, genuine schema-4 migration and
rollback, preserved evidence, paging across inserts/reopen/deleted boundaries and ordinal nonreuse.
`SessionListingTests` verifies no handle hydration/replay, independent cancellation and unchanged
creation order during execution. `SessionListingRoutesTests` exercises real HTTP/SQLite discovery
across clients/reopen plus validation, admission gates and storage failure. In an isolated copy,
storage and HTTP tests both rejected an inclusive-cursor regression; both restored controls passed.
These qualify ID discovery, not UI, activity tracking or a complete summary API.

## Read contract and incremental review order

1. [SessionSnapshot.cs](../source/backend/core/sessions/SessionSnapshot.cs) and
   [Messages.cs](../source/backend/core/sessions/Messages.cs) — detached records; shared
   `SessionExecutionResult`/`ExecutionOutcome` in [SessionExecutionResult.cs](../source/backend/core/execution/SessionExecutionResult.cs)
   are public, not duplicated. `ExecutionOutcome` describes an LLM request attempt, a tool invocation
   or a whole session execution; a successful request/tool does not imply settled execution.
2. [SessionBase.cs](../source/backend/core/sessions/SessionBase.cs) — the two public read methods
   take the existing guard; snapshots merge live admission fencing/local Stop intent with one committed view.
3. [SnapshotReader.cs](../source/backend/core/storage/SnapshotReader.cs), then
   [MessageContentReader.cs](../source/backend/core/storage/MessageContentReader.cs) — bounded queries
   in short read transactions. [DatabaseSchema.cs](../source/backend/core/storage/DatabaseSchema.cs)
   now uses schema version 5: version 4 added delivery/tool facts, sequential requests and a
   message-order index; version 5 adds only creation-order metadata. Migrations from versions
   0–4 preserve existing IDs, bytes and content cursors.
4. [SessionReadFlowTests.cs](../source/tests/Urd.Backend.Tests/sessions/SessionReadFlowTests.cs),
   [MessageContentTests.cs](../source/tests/Urd.Backend.Tests/storage/MessageContentTests.cs), and
   [SessionSnapshotStorageTests.cs](../source/tests/Urd.Backend.Tests/storage/SessionSnapshotStorageTests.cs).

Snapshots select at most 60 recent current-revision user/assistant text messages through the
message-order index, not an unbounded expansion of recent executions. Each preview is at most 256 UTF-16
units without splitting a scalar (under 64 KiB aggregate preview UTF-8). `HasOlderMessages`
labels omission; older-history navigation is not implemented. Latest settlement and unsettled
execution/Stop facts are queried independently of the recent window.

Call `ReadMessageContentAsync` with `new MessageContentCursor(messageRef, 0, 0)`.
Each page returns at most 16 KiB of original UTF-8 and examines at most 16 immutable segments. Follow `Next` while
`HasMore`; a false value means the current committed tail, not necessarily final output.
Poll snapshots and resume from that tail only when availability changes. Segment-local byte
offsets preserve Unicode and embedded NUL; stale revisions throw `HistoryRevisionConflictException`.
`Truncated` means the preview did not reach the committed segment endpoint within its budget.
`IsClosed` concerns further bytes; `FinalOutputCommitted` is a saved final marker. Neither
means successful execution settlement or finished cleanup. Continue status reads until settlement
or a diagnosed fence. HTTP read cancellation is wired; browser poll scheduling, client-side
cancellation and rendering are not built yet.

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
4. [SessionStore.cs](../source/backend/core/storage/SessionStore.cs) — opens the database and exposes
   focused parts: [Database.cs](../source/backend/core/storage/Database.cs) (connection, lock,
   transactions), [DatabaseSchema.cs](../source/backend/core/storage/DatabaseSchema.cs) (migrations),
   [SessionStorage.cs](../source/backend/core/storage/SessionStorage.cs),
   [UserMessageStorage.cs](../source/backend/core/storage/UserMessageStorage.cs) (receipts),
   [SessionExecutionStorage.cs](../source/backend/core/storage/SessionExecutionStorage.cs) and
   [LlmResponseStorage.cs](../source/backend/core/storage/LlmResponseStorage.cs) — atomic execution/request/response and delivery facts;
   these are exposed internally as `UserMessages`, `Executions` and `LlmResponses`.
   [ToolStorage.cs](../source/backend/core/storage/ToolStorage.cs) — dispatch claims and retained results;
   [LlmContextReader.cs](../source/backend/core/storage/LlmContextReader.cs) — claimed submissions and
   ordered assistant/tool context, distinct from the public text-only read projection.
5. [SessionManagerFlowTests.cs](../source/tests/Urd.Backend.Tests/sessions/SessionManagerFlowTests.cs),
   [SessionConcurrencyTests.cs](../source/tests/Urd.Backend.Tests/sessions/SessionConcurrencyTests.cs),
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
7. [SteeringFlowTests.cs](../source/tests/Urd.Backend.Tests/sessions/SteeringFlowTests.cs) — ordered steering,
   no-tool continuation, boundary races and shutdown without carryover;
   [ToolExecutionTests.cs](../source/tests/Urd.Backend.Tests/execution/ToolExecutionTests.cs) — ordinary
   errors, frozen arguments, cleanup/token ownership, malformed batches and uncertain-effect fencing;
   [ToolStorageTests.cs](../source/tests/Urd.Backend.Tests/storage/ToolStorageTests.cs) — authorization,
   sequential claims, bounded results and rollback;
   [SteeringMigrationTests.cs](../source/tests/Urd.Backend.Tests/storage/SteeringMigrationTests.cs) —
   preserved identities/cursors, atomic upgrade and reopen without replay. The user-message-receipt and
   snapshot suites also cover eight-delivery capacity, atomic message indexing and bounded selection.
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
includes its SQLite native bundle. `Microsoft.Extensions.AI.Abstractions` **10.11.0** supplies
only the request interface/data types; no provider adapter or middleware is installed.
Tests use xUnit **2.9.3**'s ordinary `[Fact]` /
`Assert` API, runner **4.0.1** (supports xUnit v2 and .NET 8+) and Microsoft.NET.Test.Sdk
**18.11.0** (.NET 8+ assets). SonarAnalyzer.CSharp **10.35.0.4138** is shared through
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
staged files; schema-migration checks and fixture-owned witness tables are the expected
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
