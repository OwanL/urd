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
  Stop source; a request-local `LlmResponseWriter` owns bounded UTF-8 batches and their flush deadline.
- Stop starts asynchronous cancellation under the guard, then joins callbacks outside it.
  Replacement requires stream cleanup, successful callback completion and saved settlement.
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
These are fake-client/Core checks, not browser polling, load or real-provider qualification.
This is a Core review checkpoint, not the browser-to-Core milestone or a complete hosting API.

## Read contract and incremental review order

1. [SessionSnapshot.cs](../source/backend/harness/sessions/SessionSnapshot.cs) and
   [Messages.cs](../source/backend/harness/sessions/Messages.cs) — detached records; shared
   `ExecutionResult`/`ExecutionOutcome` in [ExecutionResult.cs](../source/backend/harness/execution/ExecutionResult.cs)
   are public, not duplicated.
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

Paths below are relative to the repository. Classes have their own files; small data records and enums
share a file per concept. Read them alongside their owners.

1. [SessionManager.cs](../source/backend/harness/sessions/SessionManager.cs) — canonical handles,
   borrowed dependencies and the `SessionRecord` identity data used to create/load handles.
2. [SessionBase.cs](../source/backend/harness/sessions/SessionBase.cs) and
   [PrimarySession.cs](../source/backend/harness/sessions/PrimarySession.cs) — admission/driver ownership;
   [Input.cs](../source/backend/harness/sessions/Input.cs) holds the input command, receipt and enums;
   [AsyncLock.cs](../source/backend/harness/sessions/AsyncLock.cs) is the shared async lock.
3. [SessionExecution.cs](../source/backend/harness/execution/SessionExecution.cs) — one invocation,
   response streaming, cancellation and cleanup; then
   [LlmResponseWriter.cs](../source/backend/harness/execution/LlmResponseWriter.cs) — request-local
   Unicode-safe chunking and flush timing. [LlmRequest.cs](../source/backend/harness/execution/LlmRequest.cs) holds
   `LlmRequest`, `LlmResponseChunk` and `LlmRequestOutcome`; [ExecutionResult.cs](../source/backend/harness/execution/ExecutionResult.cs)
   holds the saved whole-run `ExecutionResult` and `ExecutionOutcome`. Their comments identify
   producer/consumer roles and lifecycle guarantees.
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
Test also builds Core/tests with nullable checking and the code-quality gate below.
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
regenerate both locks from `source/` with:

```powershell
& $dotnet restore Urd.slnx --force-evaluate -p:RestoreLockedMode=false --disable-parallel -m:1 -nodeReuse:false
& $dotnet restore Urd.slnx --locked-mode --disable-parallel -m:1 -nodeReuse:false
```

Review the lock changes and run tests before committing. Do not disable locked mode
for ordinary builds. SDK maintenance is a separate explicit `global.json` edit.

## Code-quality gate

Ordinary `dotnet build` and `dotnet test` commands enforce the gate in both Core and
its tests, in Debug and Release. No separate lint command, IDE extension, Sonar server
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

Analyzer upgrades are reviewed dependency changes: update the shared reference and both
locks, triage new diagnostics, then run tests. Verify the gate itself with deliberately bad
code in an isolated copy: builds and tests must exit unsuccessfully with the expected rule
IDs, and clean code must pass again after removing the probe. Passing tests or analyzers
still does not replace review of ownership, concurrency, SQL or error handling.

## Test effectiveness

Prefer improving an existing behavioral case over adding overlapping tests or fixture
machinery. Assert observable outcomes and durable state, not private helper calls or
incidental execution order. Assertions on durable state read through the shared `support/`
readers (`StoredRuns`, `StoredInputs`, `StoredOutput`, `StoredSessions`), not ad-hoc SQL, so schema evolution
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
