# Initial Core class design — internal integration reference
2026-11-05. **The three identity bites, fourth user-Send reliability contract, fifth
SessionExecution lifetime boundary, sixth coherent read surface, seventh session shutdown
and subsequent default-steering/fake-tool, public addressed Stop and saved-primary-ID
discovery increments below are owner-approved.
Other API/state shape remains proposed, not implementation permission.**
Current signatures and source ownership live in [backend development](../backend-development.md);
the wider declaration sketches later in this record are not a complete live API listing.
Checkpoint only: Windows local browser app, deterministic request-only `IChatClient`,
two primary chats/two clients, real SQLite, mid-response input, addressed Stop/settlement
and restart/reconnect without replay. No approval of the whole checkpoint,
transcript/lifecycle schema or transport beyond the [scoped hosting approvals](initial-ui-hosting-design.md).
The identity foundation, first fake-text Send/execution increment and bounded reads are
implemented; see the current implementation section below. Acceptance has real durable work behind it,
not a stub. Session shutdown now supplies the Core drain boundary needed by hosting.
Seven thin HTTP operations now expose the existing Core, including public addressed Stop
and saved-primary-ID discovery (both implemented and verified).
UI and the broader browser checkpoint remain unimplemented.
The subsequently approved steering increment now supplies durable busy
input and a minimal fake-tool continuation loop. Explicit after-run queueing, real tools/providers
and startup recovery remain separate scoped work.
Present one bite inline at a time, not this whole document or a five-type pack; approval
never extends to unpresented members.

## Phase-1 vocabulary update

The agreed breaking naming/API refactor uses user message submissions/acceptance and
session executions. Declarations below use this vocabulary; historical counts qualify
only their original checkpoints, not this refactor. Saved IDs, revisions, enum values,
SQL identifiers/schema 5, decisions and exact-retry/Stop semantics are unchanged;
there are no legacy aliases. See [current Core](../backend-development.md#breaking-api-vocabulary--phase-1)
and [HTTP](../server-development.md#session-http-contract) contracts.
Phase-2 structural cleanup is not yet included: `StoredSessionSnapshot`, the private
settlement record and the existing JSON helper remain. Wider sketches below remain
proposals where marked; renamed vocabulary does not implement them.

## Owner-approved first bite — creation/lookup only
The owner approved `SessionManager` creation/lookup after discussion: one canonical live
object per session ID; a flat kind-neutral `Dictionary<Guid, SessionBase>`; a
`SemaphoreSlim` serializing lookup/creation only, never LLM work. The presented typed
operations are `CreatePrimaryAsync()` and `GetPrimaryAsync(Guid sessionId)`, returning
`PrimarySession`. Lookup of a missing ID neither creates a record nor starts execution.
The registry stores base references; the typed primary operations construct/return a
primary and reject a non-primary target rather than treating it as one. This does not
approve delegation subtypes/relationships or a generic public lookup API.

**Still proposed:** provider selection, startup recovery and the
broader execution/Stop/view/storage-lifecycle members below, except the approved slices
recorded here: identity persistence/manager wiring, user-Send reliability, SessionExecution,
coherent reads, session shutdown, default steering/fake-tool continuation, public addressed Stop
and narrow saved-primary-ID discovery.
No other workstream or full Core-draft approval follows.

## Owner-approved second bite — identity/construction only
The owner answered **“good”** to the revised identity-only construction proposal:
```csharp
abstract class SessionBase {
    public Guid Id { get; }
    protected SessionBase(SessionRecord record);
}
sealed class PrimarySession : SessionBase {
    internal PrimarySession(SessionRecord record) : base(record) { }
}
```
Construction copies already-loaded identity/revision; no I/O or work starts. Manager
constructs a concrete primary and registers it as `SessionBase`. This bite is identity
construction only, not approval of persistence/execution coordination. Store/client
collaborators in the wider sketch below remain future proposals to review with the
behaviors requiring them; this smaller current shape is not a permanent prohibition
on those dependencies or a decision to move their responsibilities elsewhere.

The owner questioned the three-input constructor. Independent review found the inputs
reasonable for the proposed full coordinator, but premature for this identity-only
review. Keep dependencies explicit when needed; do not introduce a parameter bag,
service locator or factory merely to shorten a signature. This revised bite is now
approved for identity-only work. Store/client dependencies are now used by the approved
execution increment and direct-wiring clarification below, without changing the no-I/O rule.
Immutable already-loaded identity/revision was approved here; its narrow durable
representation and storage ownership/wiring were subsequently approved in the third bite.

## Owner-approved third bite — concrete identity persistence
```csharp
sealed class SessionStore : IDisposable {
    public static SessionStore Open(string databasePath);
    internal SessionRecord CreatePrimary(Guid sessionId);
    internal SessionRecord? ReadSession(Guid sessionId);
    public void Dispose();
}
```
`Open` opens real SQLite (WAL/NORMAL), validates the schema version and initializes
identity storage before returning a ready store; it closes acquired resources on failure.
The constructor is private and performs no I/O. Create commits before returning immutable
identity/revision; missing read returns null, never inserts. Caller owns/disposes the
store after registry calls finish; manager borrows it. This is identity persistence
only—no execution, messages or recovery.

Independent review of the owner's quality question found the boundary sound, and
recommended this small lifecycle improvement over public construction plus a separate
initialization call. It avoids exposing an uninitialized store and centralizes failure
cleanup, without introducing another factory type. The quality question was not approval;
the owner subsequently approved the improvement explicitly: **“aPPROVED, LOOKS GOOD WITH
IMPROVEMENT”**. Approval includes direct `SessionManager(SessionStore store)` wiring;
manager borrows the caller-owned store. Constructors remain no-I/O.

**Scoped implementation:** this approved boundary and direct concrete store wiring permit
one cohesive registry/identity implementation with real SQLite tests. The
caller explicitly opens/disposes the store; session constructors still take only metadata.
No mock interface, separate factory type, client dependency or full host shutdown design is needed.
Minimal Core/test project metadata and normal .NET 10/package/test-runner selection,
private SQL/helpers, schema-version marker/newer-schema rejection and resource cleanup
are routine engineering within that scope, not repeated owner votes. Tests can cover
committed create/reopen/read, missing lookup without insertion, concurrent canonical
lookup and constructor/no-execution behavior using isolated temporary databases.
No remaining material design dependency is identified for this identity-only slice.
It does not authorize other build/startup proposals, acquisition/global changes or
lifecycle schema expansion; build/test metadata is coordinated separately.

**Historical implementation evidence (identity milestone):** the first slice introduced
the concrete registry, metadata-only handles and SQLite store under `source/backend/harness/`. Session metadata
is assembly-internal; the abstract base constructor is `private protected` so only Core
subtypes consume it, without publishing a metadata API. Schema version 1 contains only
session ID/history revision; unversioned nonempty and unsupported-version databases reject.
Eight real temporary-SQLite tests passed in Release on .NET 10, with no warnings/errors:
commit/reopen, missing lookup/no insertion, concurrent canonical create/cold lookup,
metadata hydration after store disposal, duplicate insertion rollback, newer-version
rejection/resource release, unversioned-data preservation and disposed-store errors.
This is normal close/reopen evidence, not process-kill/power-loss or broader app qualification.
Independent implementation acceptance review found no supported blockers. Parent
verification also passed locked restore (lockfile hashes unchanged) and all eight Release
tests, including public-manager creation followed by store reopen and a fresh manager's
canonical lookup. No messages/executions, LLM calls or hosting were added.

## Owner-approved fourth bite — user-Send reliability contract
The owner answered **“seems reasonable”** after the inline proposal and clarification:
this is reliability of incoming user instructions, including input during a response;
not transcript management, generated assistant replies, or agent-to-agent coordination.
```csharp
// PrimarySession (current phase-1 names)
public Task<UserMessageReceipt> SubmitUserMessageAsync(UserMessageSubmission submission);
// SessionBase
public Task<UserMessageReceipt?> GetUserMessageReceiptAsync(Guid submissionId);
```
Record a stable first Accepted/Refused decision before returning a definitive receipt.
A refused ID cannot later accept a delayed original; returning an old acceptance never
starts execution again. Lookup absence remains unknown. Explicit Retry original Send
uses the unchanged ID/text/revision and may admit previously unaccepted work; reconnect
only checks receipts. Retain bounded original commands/receipts, including refusals, for
this checkpoint. These semantics and entrypoints are approved, not the full DTO/schema,
queue/disposition/driver design or UI/HTTP implementation below. Review those dependencies
before implementing an acceptance path; do not add a placeholder that accepts but cannot
execute. The subsequently approved constructor/execution ownership scope is recorded
in the fifth bite below. The narrow fake-text integration is now implemented as described
below; broader queue and context contracts remain proposed. The approved HTTP slice now uses
`UserMessageConflictException : InvalidOperationException` to identify changed-submission reuse, carrying
`SubmissionId` without changing receipt/decision policy. See the hosting record for the transport scope.

## Owner-approved fifth bite — SessionExecution lifetime boundary
The owner answered **“yeah seems reasonable i think”** to the presented `internal sealed`
`SessionExecution`; section 4 below holds its current declarations. The initial boundary
used `RunAsync`/`RequestCancellation`/`ReleaseAfterSettlement`; the subsequently approved
execution redesign below makes cancellation asynchronous and revises lifecycle ownership.
It is temporary active work;
the chat persists. Construction starts no work. SessionBase starts/observes the task and
owns admission/DB writes; execution owns the LLM stream, Stop signal and bounded output
buffer, batching updates rather than per-token writes. Browser disconnect does not cancel.
Replacement waits for actual cleanup and saved final state. Release only owned resources,
never the borrowed client. This is scoped approval, not implementation evidence or approval
of unpresented queue/schema/HTTP/composition contracts. Private helpers, fields and batching
mechanics within this boundary are engineering choices, not additional owner-review bites.
The owner asked to record the decision and parallelize work, expressing dissatisfaction with pace.

## Sixth bite — owner-approved coherent read surface

After the read proposal and explanation, the owner approved this surface on 2026-11-05.
The decision covers these read-only members on existing `SessionBase`:
```csharp
public Task<SessionSnapshot> ReadSnapshotAsync(CancellationToken readCancellation = default);
public Task<MessageContentPage> ReadMessageContentAsync(MessageContentCursor cursor,
    CancellationToken readCancellation = default);
```
Snapshots combine committed recent-message previews/revision/run facts with the live
owner's admission fence under one guard and database snapshot. Full input/output text is
read in bounded scalar-safe UTF-8 pages, not repeatedly copied into every snapshot.
Following an answer must read beyond its preview automatically, without extra clicks.
Reads start no work; cancellation only cancels the caller's guard wait, never execution.
Final output, byte closure, durable settlement and a fenced/unsettled run remain distinct.

The owner accepted **snapshot polling for the first browser increment**, rather than
owned push subscriptions now: fewer lifetime/parser/publication mechanics, at the cost
of repeated reads and refresh delay. Existing subscription/epoch/version and global-byte
cursor sketches below are superseded for this increment, not additional approved APIs.
The implementation uses flat message references and segment-local UTF-8 cursors over
immutable batches, plus the existing settlement record/enum rather than duplicate facts.
Private supporting queries, indexes/migration and tests are part of this approval.

This does not approve UI/HTTP implementation, older-history navigation, busy-input queuing,
startup recovery, public host shutdown or provider/native-context work. Read-path
implementation evidence is recorded in the backend development document after verification.

## Seventh bite — owner-approved session shutdown

On 2026-11-07 the owner approved this operation after rejecting the vague name
`ShutdownAsync`: the name must make its scope clear in isolation.
```csharp
public Task ShutdownSessionsAsync(CancellationToken waitCancellation = default);
```

`SessionManager` shuts down its live sessions, not the server or saved conversations.
The first call irreversibly fences creation/lookup and then input on every canonical
handle before requesting any cancellation. Creation/lookup then throw `InvalidOperationException`.
Already retained input decisions remain
readable and exact retries keep their original receipts; new input receives the existing
durable `Refused`/`Stopping` decision. No sessions or history are deleted or closed.

Shutdown stops all owned executions and joins LLM cleanup, cancellation callbacks and saved
settlement. A failed Stop write must not suppress local cancellation, skip that run's
drain, or prevent other sessions from stopping. Repeated calls join one retained result;
a failure cannot turn into success. Even an initially cancelled call initiates shutdown:
`waitCancellation` abandons only that caller's wait, not the underlying work. There is no
forced timeout or forced resource disposal. Reopened orphan runs are not owned executions;
shutdown neither recovers nor replays them.

Manager/session guards are not held while awaiting work or callbacks. Callbacks may
reenter reads or admission, but must not wait for the shutdown or run settlement that
requires their own completion. Store/client ownership remains with the composing caller:
it gates and drains external operations separately, and disposes resources only after
successful session shutdown and that external drain. Cancelled or failed waits prove
neither condition.

Approval is for this cohesive Core operation and its tests, not public addressed Stop,
HTTP/UI, startup recovery, session listing, schema changes or whole-host shutdown policy.
Implementation and verification are recorded in [backend development](../backend-development.md).

## Owner-approved record naming cleanup

During the Core source review, the owner approved these names individually and selected
`Llm` consistently for our LLM-facing data types (the interim `ChatRequest` therefore
became `LlmRequest`). The records remain cohesive data, not methods or new services:

| Previous name | Approved name | Meaning |
| --- | --- | --- |
| `RunSettlement` (later `ExecutionResult`) | `SessionExecutionResult` | Saved whole-execution terminal result, published after owned work and cleanup finish. |
| `RequestPlan` | `LlmRequest` | Claimed request identity and prepared messages, supplied to execution through its session. |
| `OutputBatch` | `LlmResponseChunk` | Buffered LLM response text submitted to the session for persistence. |
| `RequestCompletion` (later `LlmRequestOutcome`) | `LlmRequestResult` | Final local request-attempt result, not live/provider status or a saved acknowledgement. |

These types first lived together in `execution/ExecutionContracts.cs`; the later style pass
regrouped them by concept; their current files are `execution/LlmRequest.cs` and
`execution/SessionExecutionResult.cs`. The table uses the latest names, not the interim
`ExecutionResult`/`LlmRequestOutcome` vocabulary. Short producer/consumer comments make
roles clear. `ExecutionOutcome` keeps its name and enum values across three scopes:
one LLM request attempt, one tool invocation or a whole session execution.
Chunk members use `ResponseId` and `ChunkIndex` instead of `OutputId` and `BatchIndex`.
SDK symbols such as `IChatClient` and `ChatMessage` keep their names. This cleanup changes no behavior, schema,
ownership, cancellation or settlement guarantees; it is not approval of broader API work.

## Clear-cut readability corrections

The owner subsequently authorized clear-cut readability fixes without further per-name
approval. The user-message entry point is now `SubmitUserMessageAsync`; bounded message reads
use `ReadMessageContentAsync`, `MessageContentCursor` and `MessageContentPage`. Declarations
above and the wider sketches below use the current vocabulary; existing behavior and the
unapproved status of wider proposals remain unchanged. Storage decisions now use named
facts and explicit precedence rather than compressed predicates. Refusal-state meaning
(including the broader-than-stopping use of `Stopping`) is not silently changed here.

## Owner-approved execution redesign — 2026-11-06

During source review the owner challenged the mixed state and naming in `SessionExecution`,
explicitly permitted execution behavior changes, and approved the revised smaller ownership
model with **“Yes, implement it”**. This supersedes the earlier repeated-invocation design:

- `SessionBase`'s existing guard is the sole lifecycle authority. `SessionExecution` keeps
  its owner, borrowed client, owned Stop source and one cached cancellation-callback task;
  there is no second lock, active/released flag pair or hand-built completion source.
- At that checkpoint, each execution was invoked once and schema 3 permitted one claimed
  request per run. The later steering increment below replaces that one-request limit with
  sequential request phases while retaining one driver and Stop source per run.
- At that checkpoint Stop persisted its intent and initiated `CancelAsync` under the guard;
  public Stop below now initiates cancellation even if persistence fails. Callbacks are awaited
  outside it. The driver checks callback completion and settles under the same guard, or
  joins the cached task outside the guard before returning to settle. Successful work is
  not cancelled merely to provide a cleanup barrier. Callback failure prevents settlement.
- A fresh `LlmResponseWriter` owns one response's Unicode fragments, byte buffer, chunk
  identity/order and flush delay. It publishes through execution's guarded owner callback,
  never directly to storage. The delay is a finite task, not an owned timer requiring disposal;
  abandoning a failed response does not publish its unfinished tail.
- An unstopped run without a claim or a missing settlement result is an invariant failure
  that fences admission, not permission to spin/reinvoke the execution. Release still follows
  actual cleanup and saved settlement; the shared client is never execution-owned.

The owner approved corresponding `SessionBase` coordination changes, not new public APIs,
queuing, schema, hosting or provider behavior. Independent design and implementation reviews
passed; current verification is recorded below.

## Historical implementation — first fake-text Core checkpoint

The owner subsequently confirmed the obvious shared-client choice and requested the actual
class structure rather than another approval gate around reuse. Dependency plumbing stays
inside the existing classes:
```text
SessionManager(store, client)
  -> PrimarySession(record, store, client) : SessionBase
     -> SessionExecution(executionId, session, client)
```
Manager and sessions borrow the caller's store/client. SessionBase retains database authority;
execution calls its owner back rather than writing SQLite. No client factory, service locator,
optional-client mode or new behavioral layer was added. This is routine integration of the
approved behavior, not approval of unrelated composition/hosting contracts.

The implemented path commits an exact first decision and, for acceptance, its run in one
transaction. It claims before requesting, persists bounded UTF-8 output, requires positive
terminal evidence and settles only after cleanup. Retained decisions precede new admission
checks; lookup/retry/hydration cannot restart accepted work. Schema version 2 added the
narrow decision/run/request/output facts; version 3 adds only the indexed read paths and
preserves migration from versions 0/1/2. This is not the final transcript/view/queue schema.

The sixth bite adds coherent bounded snapshots and full-content pages over immutable
input/output segments. It exposes committed availability, final bytes and settlement
separately; read cancellation never owns LLM cancellation. No subscriptions or HTTP
state were introduced. Snapshot previews and body slices stay bounded before materialization.

Independent component and integration reviews passed. Parent locked restore and Release
build/tests pass **73/73** after the execution redesign, with no build warnings/errors.
Tests cover shared-client chats, exact retries/reopen, commit/migration rollback, Unicode/NUL
paging and chunking, held cleanup after final output, cancellation callback joins/failures,
stale Stops, persisted and live fences, and indexed recent-window selection over large
mixed histories. Tests use disposable SQLite and fake clients only; no browser/polling
performance claim. See the [read review map](../backend-development.md#read-contract-and-incremental-review-order).

That initial staging policy permitted one input per active run and refused different busy
input. The default-steering increment below supersedes that restriction. At that checkpoint,
addressed Stop/wait helpers were internal; the seventh bite adds the public manager-level session
drain. Public addressed Stop is separately approved below; settlement waiting remains internal.
Old nonterminal rows stay fenced across reopen; startup recovery is not implemented. UI/HTTP, native-context fidelity, tools
and real-provider qualification remain outside this checkpoint. The owner requested notification
at tested milestones for their own file-by-file, line-by-line review before more layers accumulate.

## Owner-approved default steering and fake-tool continuation — 2026-11-07

The owner approved moving forward with an integrated Core slice, not a second chat engine:
real SQLite, configured fake tools, no real provider or UI. During policy discussion, the
owner clarified that tool problems (such as bash syntax errors) should normally reach the
LLM, and that steering without tool calls should occur at another opportune time.
That means the next safe request boundary, not altering or cancelling an in-flight request.

- `SessionManager(store, client, tools)` optionally borrows configured `AIFunction`s and
  snapshots their collection. No tool framework, provider package or host tool configuration
  was added; existing two-argument construction still works.
- Busy input accepts into the locally owned run, up to eight nonterminal input deliveries.
  Receipts remain immutable; duplicate Sends never start another driver or duplicate delivery.
  Each request claims pending inputs atomically, in arrival order. Tool results precede those
  separate user messages; a successful no-tool response also continues if steering is waiting.
- One `SessionExecution`/Stop source and one driver remain owned per run. The driver awaits
  sequential `RunRequestAsync` phases outside the guard, then claims or settles under it.
  LLM/tool cleanup and cancellation joins still precede settlement and resource release.
- Ordinary invocation errors become saved error results and permit continuation. They do not
  prove no effects, including when binding/marshalling happens inside an opaque function.
  Core-owned encoding, persistence and cleanup failures fence instead of replaying work.
- Stop retires unclaimed steering and unstarted calls without rewriting Accepted receipts.
  There is no automatic carryover into a later run or replay on reopen.
- Schema 4 supports multiple requests, input delivery associations, tool call/result facts and
  a bounded text-message index. Migration preserves existing receipts, IDs, UTF-8 bytes and
  cursors. Public snapshots/content remain user/assistant text; tool inspection remains deferred.

Independent reviews passed for that steering checkpoint. Locked restore and **259/259 Release tests**
passed without warnings (previous evidence, not public Stop verification),
including real HTTP busy Sends, tool-result/steering order, no-tool continuation, boundary races,
Stop/shutdown joins, ordinary errors, uncertain-save fences and migration rollback. These are
fake-client/function checks, not provider-native fidelity or exactly-once external effects.
At that checkpoint public Stop remained a separate bite; its subsequent approval is recorded below.
Explicit after-run queueing, pending edits, recovery and UI remain separate work.
See [backend development](../backend-development.md) for the current review map and bounds.

## Owner-approved public addressed Stop — 2026-11-07

The owner approved stopping a conversation's current work, not closing/deleting the conversation,
with this public method and receipt shape. It is implemented and verified:
```csharp
public Task<StopReceipt> RequestStopAsync(Guid executionId);
public sealed record StopReceipt(Guid ExecutionId, StopStatus Status, ExecutionOutcome? Outcome = null);
public enum StopStatus { Requested, Settled, NotFound, Unavailable }
```
`Requested` acknowledges committed Stop intent and pending-steering retirement, not completed
cancellation. `Settled` carries the saved outcome; `NotFound` includes wrong-session IDs;
`Unavailable` means an unresolved saved run is not locally owned. Empty IDs are invalid.
Repeated Stop reports current status (Requested may become Settled), reuses cancellation and
cannot affect a replacement. Accepted input receipts stay immutable; no automatic carryover/replay.

Under the existing session guard, Core initiates cached local cancellation before attempting
Stop persistence. `SessionExecutionStorage.RequestStop(sessionId, executionId, ownsExecution)` classifies identity and
settlement before ownership, then commits Stop/retirement for an owned unresolved run in one
transaction. An unowned orphan is not mutated, recovered or falsely settled. The public method
acknowledges intent; the driver alone joins callbacks outside the guard before saved settlement.
Held LLM/tool cleanup blocks replacement; callback faults retain fenced/unsettled execution
and resources for shutdown rather than fabricating success. Shutdown keeps its existing join path.

A failed Stop write still cancels locally, fences admission and fails the request (HTTP 503),
never acknowledging durable success. Local `IsStopping` guards output and successful request
completion as well as claims/tool starts; actual tool results may still be saved. Snapshots
merge local Stop intent for owned unsettled work with persisted status: observation, not durable
acknowledgement. Existing snapshots expose stopping/fencing, run identity and saved settlement.

The hosting route accepts JSON `{}` at POST `/api/sessions/{sessionId}/executions/{executionId}/stop`:
200 Requested/Settled, 404 missing, 409 with Unavailable for an unowned unresolved run.
Disconnect does not cancel admitted Stop; HTTP never waits for callback completion.
No schema, framework, UI, real tools or child runtime is added. Full bounded-descendant
requirements are recorded in [architecture](../ARCHITECTURE.md) and
[session design](../SESSION-DESIGN.md); separately opened independent worker chats continue.
Stop does not undo effects or forcibly kill arbitrary in-process functions; functions must honor
cancellation and join their owned work. This slice does not qualify a subagent runtime.
Independent production/test reviews passed. Locked restore and **281/281 Release tests** pass
without warnings. In an isolated copy, tests detected missing local Stop write guards and a
skipped callback join; all four restored controls passed. Full evidence and review paths are in
[backend development](../backend-development.md) and [server development](../server-development.md).

## Owner-approved saved-primary-ID discovery

The owner selected saved-chat discovery before UI and approved these final names on the existing manager:
```csharp
public Task<SessionIdPage> ListPrimaryIdsAsync(
    string? cursor = null, CancellationToken cancellationToken = default);
public sealed record SessionIdPage(IReadOnlyList<Guid> SessionIds, string? NextCursor);
```
Return at most 60 IDs in newest-created order, including empty and retained unsettled chats.
The opaque string cursor is Core-owned (positive Int64 ordinal, invariant decimal); copy it
unchanged. A single indexed query uses limit 61 and an ordinal range for continuation, not
an ID lookup or offset. Each query sees committed state; pages are not a frozen snapshot.
Deleting the boundary does not break paging; refresh page one for new creations. Listing
uses the manager guard, rejects shutdown and delegates directly to `SessionStorage` without
hydrating/caching handles, reading history, starting work, recovery or persisted mutation.
Cancellation owns the guard wait/before-read check, not SQLite interruption or execution.
The caller still drains external operations before disposing the borrowed store/client.

Schema 5 adds `session_creation` with AUTOINCREMENT ordering and a unique cascading foreign
key to session identity. Migration runs after existing upgrades in their transaction, preserving
history/revisions/receipts/request/tool evidence without a root-table rebuild or disabling FKs.
Legacy rows have no creation chronology: ascending `session_id COLLATE BINARY` assigns a fixed
fallback, listed as descending binary IDs, not recovered chronology. Later creations sort ahead
of that band. Identity/order inserts commit atomically before returning/tracking a handle;
ordinals are not reused after deletion. No new close/delete API or `SessionRecord` change.

GET `/api/sessions?cursor=...` returns the page; the hosting record specifies validation/gates.
This supersedes only the old ID-order summary/`ListPrimaryAsync` discovery sketches below.
No summaries, titles, full-history navigation, last-used tracking, UI or automatic recovery
is approved. IDs prove neither usability nor stopped work; existing explicit reads supply
those details. Listing can rediscover IDs but cannot associate one with a lost-create
acknowledgement or justify automatic retry. Independent review, locked restore and
**295/295 Release tests** pass without warnings. Storage and real HTTP tests rejected an
inclusive-cursor regression in an isolated copy; restored controls passed. See
[backend evidence](../backend-development.md#saved-primary-id-discovery).

## Existing constraints, not new decisions
- [Architecture](../ARCHITECTURE.md): shared SQLite, message rows/bounded previews,
  WAL / `synchronous=NORMAL`. Returned commits survive process crash; uncommitted output
  may be lost. No power-loss, exactly-once-effect or attachment-file atomicity guarantee.
- [Session design](../SESSION-DESIGN.md): small hierarchy/guarded ownership, not frozen
  APIs. [Execution design](../EXECUTION-DESIGN.md): request-only client/Urd-owned cycle.
- ASP.NET-free Core; **Server → Core**. Server owns composition/HTTP/browser admission.
  [Application plan](../APPLICATION-PLAN.md) owns component-led layout.
- Disconnect does not cancel accepted work; Stop acknowledges intent, while replacement waits
  for actual cleanup and saved settlement. Unknown
  Send receipts reconcile, not replay. Original history remains readable.
- [SQLite validation](../SQLITE-VALIDATION.md): coherent cursors, Unicode-safe previews,
  lifecycle/revision fences and schema versioning; archived APIs are not promoted.
## Internal five-type integration sketch — not an owner-review pack
Fragments are declarations only, not compilable definitions. Names follow the approved
cleanup above; the wider member lists and behavior below remain proposals, not the current
implemented API. Named command/results are immutable data, not behavioral classes.
`Guid` identifies session/run/input/output; `long` history revision fences replacement;
`long` view version orders live views.
No future-kind, tool, provider/login/context or assignment classes or inheritance hooks.

### 1. `SessionManager` — one registry per backend/database lifetime
Owns canonical handles, recovery and shutdown fencing, not the loop. Handles remain
registered until shutdown for this two-chat checkpoint; no eviction scheme.
```csharp
private readonly SessionStore _store;
private readonly IChatClient _chatClient;
private readonly SemaphoreSlim _registryGate;
private readonly Dictionary<Guid, SessionBase> _sessions;
private bool _accepting;
public SessionManager(SessionStore store, IChatClient chatClient);
public Task InitializeAsync();
public Task<PrimarySession> CreatePrimaryAsync();
public Task<PrimarySession> GetPrimaryAsync(Guid sessionId);
public Task<SessionIdPage> ListPrimaryIdsAsync(string? cursor = null, CancellationToken cancellationToken = default);
public Task ShutdownSessionsAsync(CancellationToken waitCancellation = default);
```
Approved registry storage is flat and kind-neutral; `CreatePrimaryAsync`/`GetPrimaryAsync`
are typed primary entrypoints, not a primary-only registry. The registry gate covers
lookup/creation/discovery, never LLM execution. Startup mechanics below remain proposed;
listing is only the approved ID discovery above, and the seventh bite approves session shutdown.
Startup versions storage/reconciles nonterminal runs before serving: **zero requests
restart**. Registry serialization guarantees one handle/ID; lookup never creates IDs.
Create/uncached lookup passes the single `CreatePrimary`/`ReadSession` result into the
handle constructor under the registry gate; cached lookup needs no hydration read.
The old ID-ordered summary list is superseded by `ListPrimaryIdsAsync` above: IDs only,
newest-created ordering and opaque ordinal cursor, with no summary/status/preview contract.
Shutdown fences registry access, then calls every handle's internal input fence before
requesting Stop/awaiting runs. Cached handles cannot bypass this per-session fence.
Manager borrows the store/client and never disposes either. The composing Server/caller
gates new external access and drains admitted handlers, queries, subscriptions and read
operations; it disposes store/client only after that drain and manager quiescence,
including owned execution settlement. Cancelled, failed or timed-out waits prove neither
condition: keep resources live, never dispose underneath work. Exclusive database
ownership is a host precondition, not a lease API.

### 2. `SessionBase` (abstract) — canonical live session handle
Owns identity/run, guard, stale-write checks and bounded observation. Database facts
are authoritative; fields follow commits. No independently writable transcript cache.
```csharp
public Guid Id { get; }
private readonly SessionStore _store;
private readonly IChatClient _chatClient;
private readonly SemaphoreSlim _gate;
private long _historyRevision, _viewVersion;
private readonly Guid _viewEpoch;
private bool _accepting;
private SessionExecution? _execution;
private TaskCompletionSource<SessionExecutionResult>? _settlement;
private readonly Dictionary<Guid, (ViewLimits Limits, Channel<SessionView> Updates)> _observers;
protected SessionBase(SessionRecord record, SessionStore store, IChatClient chatClient);
public IAsyncEnumerable<SessionView> ObserveAsync(ViewLimits limits, CancellationToken observationCancellation);
public Task<HistoryPage> ReadHistoryAsync(HistoryCursor cursor, ReadLimits limits);
public Task<MessageContentPage> ReadMessageContentAsync(MessageContentCursor cursor);
public Task<UserMessageReceipt?> GetUserMessageReceiptAsync(Guid submissionId);
public Task<StopReceipt> RequestStopAsync(Guid executionId);
internal Task<SessionExecutionResult> WaitForSettlementAsync(Guid executionId, CancellationToken waitCancellation);
private protected Task<UserMessageReceipt> AdmitUserMessageAsync(UserMessageSubmission submission);
internal Task FenceUserMessagesAsync();
internal Task<LlmRequest?> ClaimNextRequestAsync(Guid executionId);
internal Task<bool> RecordOutputAsync(Guid executionId, LlmResponseChunk chunk);
internal Task<bool> CompleteRequestAsync(Guid executionId, LlmRequestResult result);
private Task DriveExecutionAsync(SessionExecution execution);
private Task<bool> TrySettleAsync(Guid executionId, ExecutionOutcome outcome);
private SessionView CaptureView(ViewLimits limits);
```
NEW proposed hydration: constructors copy ID/revision from the stored `SessionRecord`,
with no I/O or background work. Post-recovery handles start without execution/settlement
waiter and with fresh view epoch/version; retained run/settlement facts remain store-readable.
Only the handle mutates session/run state under `_gate`; SQL repeats ID/revision/lifecycle
checks. Admission resolves a retained command decision before checking `_accepting`
for new work; shutdown fences new acceptance under that same guard. While an admitted
Submit handler can still use the live store, the fence can produce a durable refusal,
not accepted work. Host refusal before Core is only an unavailable attempt.
Execution has no store access. `DriveExecutionAsync` owns/observes the pump and failures.
Never await LLM work, cancellation callbacks, settlement or subscribers under the guard.
The current driver invokes `RunRequestAsync` sequentially on one owned execution. After each
request/tool phase, it joins outstanding cancellation callbacks outside the guard, then claims
the next request or settles under the guard. Admission cannot slip between the pending-input
check and settlement, and a late Stop cannot escape the join. Saved settlement releases run
resources and completes waiters; missing settlement or uncertain writes fence rather than replay.
An ambiguous request/run terminal commit prevents continuation and keeps admission fenced
pending reconciliation; no false idle.
### 3. `PrimarySession : SessionBase` — ongoing primary input policy
Handle lifetime; no extra mutable fields. Chooses ordinary-input policy, not another lock.
```csharp
internal PrimarySession(SessionRecord record, SessionStore store, IChatClient chatClient);
public Task<UserMessageReceipt> SubmitUserMessageAsync(UserMessageSubmission submission);
```
**Owner direction, 2026-11-07 — steering by default, now implemented:** ordinary
input during active work defaults to steering. The owner asked to send it as a message
alongside whatever batch of tool-call results is sent to the LLM next. This supersedes
the earlier proposal to infer follow-up versus steering from arrival before/after the
initial request claim. Idle input still starts work; steering does not mutate an in-flight
request. Tool results and steering remain distinct messages in the next LLM request.

The owner subsequently approved the integrated fake-tool slice and clarified no-tool delivery:
pending steering triggers the next request after the current response finishes, rather than
waiting for a tool call. Durable claim order and Stop/failure retirement are implemented as
recorded above. Explicit after-run queueing remains unimplemented and needs its own design.
Stopping durably refuses new valid input before acceptance, with no run/message
creation, and leaves its draft client-owned; existing decisions remain readable.
No pending-edit, suffix-edit, snooze or final-close API is implied or removed here.

### 4. `SessionExecution` — fifth-approved temporary owned async work boundary
Owns response streaming and cancellation, not admission or transcript storage. Response
buffering belongs to its request-local `LlmResponseWriter`. One execution per active run;
no thread/process per session or global request queue. Construction starts no work.
SessionBase invokes sequential request phases outside its guard and observes all cleanup.
```csharp
internal Guid ExecutionId { get; }
internal bool IsStopping { get; }
internal SessionExecution(Guid executionId, SessionBase session, IChatClient chatClient,
    IReadOnlyList<AIFunction>? tools = null);
internal Task<ExecutionOutcome> RunRequestAsync(LlmRequest request);
internal Task StopAsync();
internal Task StopCallbacks { get; }
public void Dispose();
```
A successfully returned invocation has quiesced its LLM/enumerator, writer timer and
append work; it is not durable settlement or a join of run cancellation callbacks.
SessionBase serializes cancellation/release under its guard and joins `StopCallbacks`
outside it before saving settlement. Repeated Stop calls share that task; a stale run ID
cannot cancel a replacement. No direct store access: owner retains claim/record/outcome/
publication authority. The fifth approval and subsequent execution redesign cover this
boundary, not every SessionBase public contract or storage rule. Substantive ownership,
concurrency or persistence changes still need review.
Uses its Stop token, never HTTP/observer tokens. Receives a durably claimed request from
its owner and records output/call/result facts through that owner. Completion does not close chat.
A successful request requires positive terminal-success evidence (explicit in the fake
client), not clean enumeration, complete-looking text or absence of an exception; missing,
failed or incomplete terminal evidence cannot yield success. Each claimed request records
its own terminal outcome through `CompleteRequestAsync` before any continuation or normal
run settlement; output-item completion alone is not request success. Tools remain out of
this checkpoint.
Response cleanup is awaited before returning; the session additionally joins cancellation
callbacks. Noncooperation leaves Stop **stopping**, blocking replacement. Lifecycle commands
rely on the sole session owner rather than another execution-level lock.
Release owned resources only after invocation/callback exit and confirmed durable settlement;
ambiguous settlement keeps admission fenced. Never dispose the borrowed client.

### 5. `SessionStore` — database lifetime; intent-specific transactions
Owns SQL/schema, serialized writer and short independent readers; no shared concurrent
connection or transaction across LLM work/observation. Calls are synchronous/bounded.
```csharp
private readonly SqliteConnection _writer;
private readonly object _writeGate;
private readonly string _connectionString;
private SessionStore(SqliteConnection writer);
public static SessionStore Open(string databasePath);
internal void InitializeAndRecover();
internal SessionRecord CreatePrimary(Guid sessionId);
internal SessionRecord? ReadSession(Guid sessionId);
// ID discovery is owned by SessionStorage.ListPrimaryIds(string? cursor), not a summary API.
// Current focused owners: UserMessages.Admit/ReadReceipt, Executions.ClaimNextRequest/
// RequestStop/TrySettle/ReadSettlement, LlmResponses.Append/CompleteRequest.
// The signatures below remain the wider, unimplemented store-facade sketch, not live members.
internal (UserMessageReceipt Receipt, bool NewlyAccepted) AdmitUserMessage(Guid sessionId, long historyRevision, UserMessageSubmission submission);
internal UserMessageReceipt? ReadUserMessageReceipt(Guid sessionId, Guid submissionId);
internal LlmRequest? ClaimNextRequest(Guid sessionId, Guid executionId, long historyRevision);
internal WriteReceipt AppendOutput(Guid sessionId, Guid executionId, long historyRevision, LlmResponseChunk chunk);
internal WriteReceipt CompleteRequest(Guid sessionId, Guid executionId, long historyRevision, LlmRequestResult result);
internal StopReceipt RequestStop(Guid sessionId, Guid executionId, bool ownsExecution);
internal SettlementAttempt TrySettle(Guid sessionId, Guid executionId, ExecutionOutcome outcome);
internal SessionView ReadView(Guid sessionId, ViewLimits limits);
internal HistoryPage ReadHistory(Guid sessionId, HistoryCursor cursor, ReadLimits limits);
internal MessageContentPage ReadMessageContent(Guid sessionId, MessageContentCursor cursor);
internal SessionExecutionResult? ReadSettlement(Guid sessionId, Guid executionId);
public void Dispose();
```
## Coordinated input → stream → Stop → recovery flow
`UserMessageReceipt` correlates session/submission ID and the exact original submission (expected
history revision and bounded decoded text, without normalization). Its admission decision
is immutable **Accepted or Refused**: refusal has a reason and no admitted run; acceptance
has its original run/disposition and separately evolving request/terminal evidence.
Accepted never means LLM success, and later interruption never changes it to Refused.
`SessionExecutionResult` retains execution ID and the `Completed`, `Cancelled` or `Failed` outcome.
1. **Decide input — reliability contract approved; queue/schema details still proposed:**
   `UserMessageSubmission` carries
   stable submission ID, expected history revision and text. For an existing session and valid
   command, one guarded store transaction resolves the ID or commits its first decision:
   - Existing ID with identical text **and original revision** returns retained evidence
     before today's capacity/revision/stopping checks. Conflicting reuse is an attempt
     error, never an overwrite or a refusal of the original command.
   - New acceptance commits input/receipt, steering/follow-up disposition and run association
     together (creating the active run only if idle). Only `NewlyAccepted`, not merely a
     returned Accepted receipt, may trigger new execution work. The flag is internal,
     per-call evidence, not part of the durable decision or public receipt.
   - New capacity/stale-revision/stopping refusal commits its receipt and matching command
     before returning. No accepted submission/message/execution or LLM work is created; a delayed
     original must return this same refusal even after capacity becomes available.
   Core enforces **16 KiB UTF-8/input** and **eight nonterminal accepted inputs/session**,
   including claimed inputs awaiting outcome; admission repeats the capacity check in its
   transaction. Refused IDs do not consume those accepted-work slots. Receipt/matching
   payloads survive reopen and are not expired in this checkpoint: bounded per command,
   but disk grows with decided IDs. This is not a permanent product retention policy;
   revisit matching/retention before history edits or final closure are introduced.
   Missing session, malformed/oversize command, conflicting reuse, host rejection before
   Core, or unavailable/ambiguous storage is not a durable refusal of an unknown original.
   Receipt lookup returns retained evidence or `null` only after successful snapshot
   absence; null cannot exclude an older in-flight attempt. An explicit user retry may
   submit previously unaccepted work, but must reuse the exact original command. A matched
   committed decision resolves uncertainty; no HTTP status or missing receipt substitutes
   for it. No automatic resubmission on reconnect, changed-revision retry or new ID for an
   unresolved Send. After confirmed refusal, a deliberate fresh Send may use a new ID.
   Eventual tests use the real admission owner: loss before admission; acceptance/refusal
   commit with lost reply and reopen; retry refused before delayed original arrives;
   revision change before versus after a decision; identical/conflicting concurrent calls.
   Assert retained decisions, no accepted work for a refused ID, and no restart from a
   returned old acceptance. No executable qualification of this new proposal yet.
2. **Deliver/record:** request claim atomically records which queued inputs belong to
   that request. Claiming is not proof of provider receipt/consumption or a completed
   answer; the receipt must retain the request outcome rather than imply success.
   `LlmRequest` carries request identity, revision and prepared ordered LLM messages,
   not UI state. `LlmRequestResult` carries that request ID and completed/failed/cancelled
   outcome; success requires the validated positive terminal evidence above, even for an
   empty response. Its guarded transaction checks request/run/revision and commits the
   request outcome plus associated input receipts independently of run settlement. Identical
   completion returns retained evidence; conflicting/stale completion rejects. A stopping
   run cannot newly record success, but may record failed/cancelled cleanup outcomes.
   NEW batching proposal: execution coalesces source updates into scalar-safe batches
   within **16 KiB UTF-8**, flushing on size, short elapsed-time deadline or normal request
   end. Start the deadline when the buffer becomes nonempty; tiny updates never postpone
   it. The timer can flush while the next source update is awaited. Commit each flushed
   batch, not each provider chunk, **before** publication; no provisional UI tail here.
   `LlmResponseChunk` has request/response IDs, chunk index, text and response final marker;
   append checks request/run/revision/stopping and expected next index. Slice oversized
   updates without exceeding the buffer cap; writes apply backpressure. Retain at most
   one pending source read and one timer wait: a timer win does not start a second read.
   Serialize/await appends and observe losing wait tasks; request cleanup quiesces timer,
   enumerator and in-flight append before returning, outside the session guard. Stop
   cannot force a flush past the stopping fence; unpublished tail is not durable output.
   No exact timing/latency or per-token fsync promise. This reduces publication pressure,
   not proof of bounded view-construction/guard costs; measure those on the integrated path.
3. **Observe:** under `_gate`, register observer and capture its initial view together.
   `ReadView` reads metadata, window and cursor in one short SQLite read snapshot.
   Subsequent mutations commit before publishing replacement views under the same guard.
   Capacity-one channels replace pending views: no delta-gap recovery or slow-client
   growth. `SessionView` has handle epoch/view version, revision, row cursor, run/Stop
   status, pending-input previews and committed output IDs/offsets, plus nullable
   `LastSettlement` (run ID/outcome) even when no run is active. It is the latest retained
   terminal run, survives recovery and remains distinct from any newer active run.
   Row, preview, pending and total-byte caps apply; lazy history reads use one snapshot
   and revisioned cursors.
   NEW defaults: 60 rows, 256 UTF-16 units/preview (surrogate-safe), 64 KiB/view or history
   page, 16 KiB/output batch. Truncation indicates more content, never loses identity.
   Reader disposal unregisters only itself. Reconnect starts a fresh view, not event replay.
   `ReadHistory` remains a row/preview window; full original text uses the one lazy content
   query below rather than lifting preview caps.

   **Bounded message content:** immutable `MessageContentCursor(RowId, HistoryRevision, Utf8Offset)`
   starts at offset zero using the stable row ID/revision from a view/history item.
   `MessageContentPage` returns that identity/revision, `FromUtf8Offset`, decoded `Text`,
   `NextCursor`, `AvailableThroughUtf8Offset`, `HasMore` and `IsFinal`. Each call reads one
   short snapshot and returns at most **16 KiB UTF-8 text**, ending on a Unicode scalar
   boundary. Offsets count original unescaped UTF-8 bytes; invalid/interior-scalar offsets
   or offsets beyond that snapshot's available tail reject. No normalization or replacement
   characters are introduced. A cursor at the tail returns empty text without advancing.
   `HasMore` means unread bytes in this snapshot; `IsFinal` means the row will not grow.
   `NextCursor` is always returned, including at a nonfinal tail: reuse it after a newer
   view advertises appended bytes. Appending preserves row identity/revision/cursors;
   suffix replacement changes history revision and rejects stale cursors explicitly,
   never silently retargeting an ordinal. Missing row/session is distinct from invalidation.
   Metadata and bounded byte range are read in the same snapshot. Store must seek directly
   into persisted row text/bounded output segments (indexed segment/byte offsets and
   incremental BLOB reads, or equivalent bounded range access), decoding only this range
   plus at most one scalar of lookahead. Never deserialize a whole message envelope,
   scan/materialize an arbitrarily large body, or read all preceding segments then truncate.
   This constrains message-row content access, not a new store/service or finalized schema;
   accepted input and output batches already have the 16 KiB UTF-8 bound.
4. **Stop/settle:** public `RequestStopAsync(executionId)` initiates cached local cancellation
   under the guard even if persistence fails, then attempts durable Stop/retirement of pending
   steering. Requested acknowledges intent, Settled carries saved outcome, NotFound includes
   wrong-session IDs, and Unavailable leaves an unowned unresolved run untouched. Failed writes
   fence and fail, not acknowledge. The driver joins callbacks outside the guard; only cleanup
   and saved settlement permit replacement. Stale Stop cannot affect a newer run. Internal
   settlement-wait cancellation cancels only the wait.
5. **Restart:** versioned transactional upgrades reject newer schemas. Recovery preserves
   both accepted/refused admission decisions and their command matching data, alongside
   committed request outcomes/receipts; it marks prior active runs and nonterminal requests,
   partial output and undelivered accepted input interrupted, retaining receipt identities
   and committed order; none is automatically redelivered. Reconnect
   queries unresolved receipts before any deliberate new Send. This does not decide future
   due-never-dispatched follow-ups or authorize replay of missing external-effect results.

## Owner review gates / limits
- Approved scopes are manager creation/lookup, identity-only construction, narrow concrete
  identity persistence/store wiring, fourth user-Send reliability and fifth SessionExecution
  lifetime/ownership boundary above, with the direct borrowed-dependency wiring clarified
  above. The current narrow fake-text increment is implemented, not the whole checkpoint.
  Unpresented public contracts, queue policy, full transcript schema, commit-before-publish and
  bounded replacement views remain proposals; routine private helpers need no extra vote;
  fourth/fifth approval does not authorize the whole Send pipeline or accepting stubs.
- Eviction/context/view costs need evidence; UI caps are not context caps/latency targets.
  Transport/browser gate/wire DTOs need a separate pack; navigation retains its ≤100 LOC gate.
- Unconfirmed cleanup/DB outcomes stay visible. Providers/tools, edits/closure and their
  uncertainty exceed this pack: flag critical complexity before expanding APIs.
