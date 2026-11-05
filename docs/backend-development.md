# Backend development

The current Core checkpoint includes session identity, durable user-Send decisions,
fake-text execution and bounded snapshot/content reads through real SQLite. Core remains an ASP.NET-free .NET library;
there is no runnable Server or UI yet.

## Checkpoint scope

- `SessionManager(store, chatClient)` passes the same borrowed dependencies to canonical
  session handles. Constructors start no work. The caller owns both resources.
- Acceptance and run identity commit together. Exact retries return the original decision;
  neither lookup nor retained acceptance starts work. Refusal fences delayed originals.
- `SessionBase` owns admission and database writes; temporary `SessionExecution` owns
  streaming, bounded UTF-8 batches and Stop. Replacement requires cleanup and saved settlement.
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
The approved read increment passes **61/61** Release tests after locked restore, with no
build warnings/errors. Coverage adds scalar-safe full-content paging, committed tail following,
final output during held cleanup, reopened/faulted fences, exact recent-window boundaries,
indexed selection over 9,000 mixed-history runs, and atomic version-2-to-3 migration/rollback.
These are fake-client/Core checks, not browser polling, load or real-provider qualification.
This is a Core review checkpoint, not the browser-to-Core milestone or a complete hosting API.

## Read contract and incremental review order

1. [SessionSnapshot.cs](../source/backend/harness/sessions/SessionSnapshot.cs) — detached records;
   existing `RunSettlement`/`ExecutionOutcome` are public, not duplicated.
2. [SessionBase.cs](../source/backend/harness/sessions/SessionBase.cs) — the two public read methods
   take the existing guard; snapshots merge the live admission fence with one committed view.
3. [SessionStore.Snapshots.cs](../source/backend/harness/storage/SessionStore.Snapshots.cs), then
   [SessionStore.Content.cs](../source/backend/harness/storage/SessionStore.Content.cs) — bounded queries
   in short read transactions. [SessionStore.cs](../source/backend/harness/storage/SessionStore.cs)
   adds two indexes in schema version 3, preserving version 0/1/2 migrations.
4. [SessionReadFlowTests.cs](../source/tests/Urd.Backend.Tests/SessionReadFlowTests.cs),
   [RowContentTests.cs](../source/tests/Urd.Backend.Tests/RowContentTests.cs), and
   [SessionSnapshotStorageTests.cs](../source/tests/Urd.Backend.Tests/SessionSnapshotStorageTests.cs).

Snapshots return at most 60 recent current-revision messages, each preview at most 256 UTF-16
units without splitting a scalar (under 64 KiB aggregate preview UTF-8). `HasOlderMessages`
labels omission; older-history navigation is not implemented. Latest settlement and unsettled
run/Stop facts are queried independently of the recent window.

Start a body read at `new RowContentCursor(messageRef, 0, 0)`. Each page returns at most
16 KiB of original UTF-8 and examines at most 16 immutable segments. Follow `Next` while
`HasMore`; a false value means the current committed tail, not necessarily final output.
Poll snapshots and resume from that tail only when availability changes. Segment-local byte
offsets preserve Unicode and embedded NUL; stale revisions throw `HistoryRevisionConflictException`.
`Truncated` means the preview did not reach the committed segment endpoint within its budget.
`IsClosed` concerns further bytes; `FinalOutputCommitted` is a saved final marker. Neither
means successful run settlement or finished cleanup. Continue status reads until settlement
or a diagnosed fence. Poll scheduling/HTTP cancellation and browser rendering are not built yet.

## Review order

Paths below are relative to the repository. Read the small data records alongside their owners.

1. [SessionManager.cs](../source/backend/harness/sessions/SessionManager.cs) — canonical handles
   and borrowed dependencies; [SessionRecord.cs](../source/backend/harness/sessions/SessionRecord.cs).
2. [SessionBase.cs](../source/backend/harness/sessions/SessionBase.cs) and
   [PrimarySession.cs](../source/backend/harness/sessions/PrimarySession.cs) — admission/driver ownership;
   [InputCommand.cs](../source/backend/harness/sessions/InputCommand.cs) and
   [InputReceipt.cs](../source/backend/harness/sessions/InputReceipt.cs).
3. [SessionExecution.cs](../source/backend/harness/execution/SessionExecution.cs) — streaming,
   buffering, cancellation and cleanup; [ExecutionContracts.cs](../source/backend/harness/execution/ExecutionContracts.cs).
4. [SessionStore.cs](../source/backend/harness/storage/SessionStore.cs) — opening/migration/receipts;
   [SessionStore.Execution.cs](../source/backend/harness/storage/SessionStore.Execution.cs) — atomic run/request/output facts.
5. [SessionManagerFlowTests.cs](../source/tests/Urd.Backend.Tests/SessionManagerFlowTests.cs),
   [SessionConcurrencyTests.cs](../source/tests/Urd.Backend.Tests/SessionConcurrencyTests.cs) and
   [SessionExecutionTests.cs](../source/tests/Urd.Backend.Tests/SessionExecutionTests.cs) — whole Core flow,
   concurrent retries, cancelled waiters and lifecycle failures.
6. [InputReceiptTests.cs](../source/tests/Urd.Backend.Tests/InputReceiptTests.cs),
   [ExecutionStorageTests.cs](../source/tests/Urd.Backend.Tests/ExecutionStorageTests.cs) and
   [SessionIdentityTests.cs](../source/tests/Urd.Backend.Tests/SessionIdentityTests.cs) — storage/registry invariants;
   [TemporaryDatabase.cs](../source/tests/Urd.Backend.Tests/TemporaryDatabase.cs) supplies isolated test data.

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
