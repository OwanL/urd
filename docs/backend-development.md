# Backend development

The current Core checkpoint includes session identity, durable user-Send decisions,
fake-text execution and bounded snapshot/content reads through real SQLite. Core remains an ASP.NET-free .NET library;
there is no runnable Server or UI yet.

## Checkpoint scope

- `SessionManager(store, chatClient)` passes the same borrowed dependencies to canonical
  session handles. Constructors start no work. The caller owns both resources.
- `PrimarySession.SubmitUserInputAsync(input)` returns a durable acceptance/refusal receipt,
  not an LLM answer. Acceptance and run identity commit together. Exact retries return
  the original decision; neither lookup nor retained acceptance starts work. Refusal fences delayed originals.
- `SessionBase` owns admission, database writes and lifecycle coordination under its existing guard.
  It invokes each `SessionExecution` once, outside that guard. Execution owns the model stream and
  Stop source; a request-local `LlmResponseWriter` owns bounded UTF-8 batches and their flush timer.
- Stop starts asynchronous cancellation under the guard, then joins callbacks outside it.
  Replacement requires stream/timer cleanup, successful callback completion and saved settlement.
  Callback failures or an unexpected missing claim/settlement fence the run; they do not retry it.
- This development increment admits one input per active run. A different busy input gets
  a durable Capacity/Stopping refusal, not a queue slot. Mid-response delivery is not implemented.
- Public snapshot/content reads expose committed state without starting work; cancelled reads
  do not cancel execution. Reopened orphan runs and live persistence faults are reported as fenced.
- Stop/settlement helpers are internal. Public shutdown, push subscriptions, HTTP/UI, tool/native-context
  fidelity and real-provider qualification remain pending. An admission receipt is not completion.
- Normal terminal close/reopen is tested without replay. Nonterminal rows survive reopening
  and block new admission; startup interruption/recovery is **not** implemented.

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
These are fake-client/Core checks, not browser polling, load or real-provider qualification.
This is a Core review checkpoint, not the browser-to-Core milestone or a complete hosting API.

## Read contract and incremental review order

1. [SessionSnapshot.cs](../source/backend/harness/sessions/SessionSnapshot.cs) and its neighbouring
   message records (`MessagePreview`, `MessageRef`, `MessageContentCursor`, `MessageContentPage`) —
   detached records; shared `ExecutionResult`/`ExecutionOutcome` in `execution/` are public, not duplicated.
2. [SessionBase.cs](../source/backend/harness/sessions/SessionBase.cs) — the two public read methods
   take the existing guard; snapshots merge the live admission fence with one committed view.
3. [SnapshotReader.cs](../source/backend/harness/storage/SnapshotReader.cs), then
   [MessageContentReader.cs](../source/backend/harness/storage/MessageContentReader.cs) — bounded queries
   in short read transactions. [DatabaseSchema.cs](../source/backend/harness/storage/DatabaseSchema.cs)
   adds two indexes in schema version 3, preserving version 0/1/2 migrations.
4. [SessionReadFlowTests.cs](../source/tests/Urd.Backend.Tests/sessions/SessionReadFlowTests.cs),
   [MessageContentTests.cs](../source/tests/Urd.Backend.Tests/storage/MessageContentTests.cs), and
   [SessionSnapshotStorageTests.cs](../source/tests/Urd.Backend.Tests/storage/SessionSnapshotStorageTests.cs).

Snapshots return at most 60 recent current-revision messages, each preview at most 256 UTF-16
units without splitting a scalar (under 64 KiB aggregate preview UTF-8). `HasOlderMessages`
labels omission; older-history navigation is not implemented. Latest settlement and unsettled
run/Stop facts are queried independently of the recent window.

Call `ReadMessageContentAsync` with `new MessageContentCursor(messageRef, 0, 0)`.
Each page returns at most 16 KiB of original UTF-8 and examines at most 16 immutable segments. Follow `Next` while
`HasMore`; a false value means the current committed tail, not necessarily final output.
Poll snapshots and resume from that tail only when availability changes. Segment-local byte
offsets preserve Unicode and embedded NUL; stale revisions throw `HistoryRevisionConflictException`.
`Truncated` means the preview did not reach the committed segment endpoint within its budget.
`IsClosed` concerns further bytes; `FinalOutputCommitted` is a saved final marker. Neither
means successful run settlement or finished cleanup. Continue status reads until settlement
or a diagnosed fence. Poll scheduling/HTTP cancellation and browser rendering are not built yet.

## Review order

Paths below are relative to the repository. Each type has its own file named after it; read the
small data records alongside their owners.

1. [SessionManager.cs](../source/backend/harness/sessions/SessionManager.cs) — canonical handles,
   borrowed dependencies and the `SessionRecord` identity data used to create/load handles.
2. [SessionBase.cs](../source/backend/harness/sessions/SessionBase.cs) and
   [PrimarySession.cs](../source/backend/harness/sessions/PrimarySession.cs) — admission/driver ownership;
   `InputCommand`, `InputReceipt`, `InputDecision` and `InputRefusalReason` sit beside them;
   [AsyncLock.cs](../source/backend/harness/sessions/AsyncLock.cs) is the shared async lock.
3. [SessionExecution.cs](../source/backend/harness/execution/SessionExecution.cs) — one invocation,
   response streaming, cancellation and cleanup; then
   [LlmResponseWriter.cs](../source/backend/harness/execution/LlmResponseWriter.cs) — request-local
   Unicode-safe chunking and flush timing. The data types `LlmRequest`, `LlmResponseChunk`,
   `LlmRequestOutcome`, saved whole-run `ExecutionResult`, and `ExecutionOutcome` sit beside them;
   their header comments identify producer/consumer roles and lifecycle guarantees.
4. [SessionStore.cs](../source/backend/harness/storage/SessionStore.cs) — opens the database and exposes
   focused parts: [Database.cs](../source/backend/harness/storage/Database.cs) (connection, lock,
   transactions), [DatabaseSchema.cs](../source/backend/harness/storage/DatabaseSchema.cs) (migrations),
   [SessionStorage.cs](../source/backend/harness/storage/SessionStorage.cs),
   [InputStorage.cs](../source/backend/harness/storage/InputStorage.cs) (receipts),
   [RunStorage.cs](../source/backend/harness/storage/RunStorage.cs) and
   [OutputStorage.cs](../source/backend/harness/storage/OutputStorage.cs) — atomic run/request/output facts.
5. [SessionManagerFlowTests.cs](../source/tests/Urd.Backend.Tests/sessions/SessionManagerFlowTests.cs),
   [SessionConcurrencyTests.cs](../source/tests/Urd.Backend.Tests/sessions/SessionConcurrencyTests.cs),
   [LlmResponseWriterTests.cs](../source/tests/Urd.Backend.Tests/execution/LlmResponseWriterTests.cs) and
   [SessionExecutionTests.cs](../source/tests/Urd.Backend.Tests/execution/SessionExecutionTests.cs) — whole Core flow,
   concurrent retries, cancelled waiters and lifecycle failures.
6. [InputReceiptTests.cs](../source/tests/Urd.Backend.Tests/storage/InputReceiptTests.cs),
   [ExecutionStorageTests.cs](../source/tests/Urd.Backend.Tests/storage/ExecutionStorageTests.cs) and
   [SessionIdentityTests.cs](../source/tests/Urd.Backend.Tests/sessions/SessionIdentityTests.cs) — storage/registry invariants;
   [TemporaryDatabase.cs](../source/tests/Urd.Backend.Tests/support/TemporaryDatabase.cs) supplies isolated test data.
   `support/` also holds the scripted model client, response scripts and execution fixture;
   test folders mirror the source folders.

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
Test also builds Core/tests with nullable checking and compiler warnings as errors.
The command avoids persistent build/compiler servers and limits build parallelism.

`Microsoft.Data.Sqlite` **10.0.14** follows the stable .NET 10 provider line and
includes its SQLite native bundle. `Microsoft.Extensions.AI.Abstractions` **10.11.0** supplies
only the request interface/data types; no provider adapter or middleware is installed.
Tests use xUnit **2.9.3**'s ordinary `[Fact]` /
`Assert` API, runner **4.0.1** (supports xUnit v2 and .NET 8+) and Microsoft.NET.Test.Sdk
**18.11.0** (.NET 8+ assets). Versions live only at their project references; lockfiles
retain the transitive graph and content hashes. No central version layer is needed yet.

For an intentional reviewed dependency update only, edit the project reference and
regenerate both locks from `source/` with:

```powershell
& $dotnet restore Urd.slnx --force-evaluate -p:RestoreLockedMode=false --disable-parallel -m:1 -nodeReuse:false
& $dotnet restore Urd.slnx --locked-mode --disable-parallel -m:1 -nodeReuse:false
```

Review the lock changes and run tests before committing. Do not disable locked mode
for ordinary builds. SDK maintenance is a separate explicit `global.json` edit.

## Outputs and data

Normal generated `bin/`, `obj/` and test results stay inside the repository and are
ignored. NuGet uses its standard package cache. Tests own disposable temporary
SQLite directories; they must not open personal sessions, credentials or copy `data/`.
Build/test never resets application data. No clean, package or activation workflow
is implemented by this slice. During coordinated agent work, restore/build/test
must acquire, start and release the exact exclusive resource `urd:dotnet`.
