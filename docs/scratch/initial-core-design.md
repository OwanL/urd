# Initial Core class design — internal integration reference
2026-11-05. **The three identity bites, fourth user-Send reliability contract, fifth
SessionExecution lifetime boundary and sixth coherent read surface below are owner-approved.
Other API/state shape remains proposed, not implementation permission.**
Checkpoint only: Windows local browser app, deterministic request-only `IChatClient`,
two primary chats/two clients, real SQLite, mid-response input, addressed Stop/settlement
and restart/reconnect without replay. No approval of the whole checkpoint,
transcript/lifecycle schema or transport.
The identity foundation, first fake-text Send/execution increment and bounded reads are
implemented; see the current implementation section below. Acceptance has real durable work behind it,
not a stub. Busy-input queuing, startup recovery, public hosting/lifecycle APIs and the
broader browser checkpoint remain unimplemented.
Present one bite inline at a time, not this whole document or a five-type pack; approval
never extends to unpresented members.

## Owner-approved first bite — creation/lookup only
The owner approved `SessionManager` creation/lookup after discussion: one canonical live
object per session ID; a flat kind-neutral `Dictionary<Guid, SessionBase>`; a
`SemaphoreSlim` serializing lookup/creation only, never model work. The presented typed
operations are `CreatePrimaryAsync()` and `GetPrimaryAsync(Guid sessionId)`, returning
`PrimarySession`. Lookup of a missing ID neither creates a record nor starts execution.
The registry stores base references; the typed primary operations construct/return a
primary and reject a non-primary target rather than treating it as one. This does not
approve delegation subtypes/relationships or a generic public lookup API.

**Still proposed:** provider selection, startup recovery, listing, shutdown and the
broader execution/Stop/view/storage-lifecycle members below, except
the narrow identity persistence/manager wiring in the third bite, user-Send reliability
contract in the fourth and SessionExecution boundary in the fifth. No other workstream
or full Core-draft approval follows.

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
canonical lookup. No messages/executions, model calls or hosting were added.

## Owner-approved fourth bite — user-Send reliability contract
The owner answered **“seems reasonable”** after the inline proposal and clarification:
this is reliability of incoming user instructions, including input during a response;
not transcript management, generated assistant replies, or agent-to-agent coordination.
```csharp
// PrimarySession
public Task<InputReceipt> SubmitAsync(InputCommand command);
// SessionBase
public Task<InputReceipt?> GetInputReceiptAsync(Guid inputId);
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
below; broader queue, context and hosting contracts remain proposed.

## Owner-approved fifth bite — SessionExecution lifetime boundary
The owner answered **“yeah seems reasonable i think”** to the presented `internal sealed`
`SessionExecution`; section 4 below holds its declarations (`RunId`, constructor and
`RunAsync`/`RequestCancellation`/`ReleaseAfterSettlement`). It is temporary active work;
the chat persists. Construction starts no work. SessionBase starts/observes the task and
owns admission/DB writes; execution owns the model stream, Stop signal and bounded output
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
public Task<RowContentPage> ReadRowContentAsync(RowContentCursor cursor,
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

## Owner-approved record naming cleanup

During the Core source review, the owner approved these names individually and selected
`Llm` consistently for our model-facing data types (the interim `ChatRequest` therefore
became `LlmRequest`). The records remain cohesive data, not methods or new services:

| Previous name | Approved name | Meaning |
| --- | --- | --- |
| `RunSettlement` | `ExecutionResult` | Saved whole-run terminal result, published after owned work and cleanup finish. |
| `RequestPlan` | `LlmRequest` | Claimed request identity and prepared messages, supplied to execution through its session. |
| `OutputBatch` | `LlmResponseChunk` | Buffered LLM response text submitted to the session for persistence. |
| `RequestCompletion` | `LlmRequestOutcome` | Final local request-attempt outcome, not live/provider status or a saved acknowledgement. |

The owner prefers these related types together in `execution/ExecutionContracts.cs`,
not one file per type or another directory. A file-purpose note and short producer/consumer
comments make their roles clear. `ExecutionOutcome` keeps its name and enum values.
Chunk members use `ResponseId` and `ChunkIndex` instead of `OutputId` and `BatchIndex`.
SDK symbols such as `IChatClient` and `ChatMessage` keep their names. This cleanup changes no behavior, schema,
ownership, cancellation or settlement guarantees; it is not approval of broader API work.

## Current implementation — first fake-text Core checkpoint

The owner subsequently confirmed the obvious shared-client choice and requested the actual
class structure rather than another approval gate around reuse. Dependency plumbing stays
inside the existing classes:
```text
SessionManager(store, client)
  -> PrimarySession(record, store, client) : SessionBase
     -> SessionExecution(runId, session, client)
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
separately; read cancellation never owns model cancellation. No subscriptions or HTTP
state were introduced. Snapshot previews and body slices stay bounded before materialization.

Independent component and integration reviews passed. Parent locked restore and Release
build/tests pass **61/61**, with no build warnings/errors. Tests cover shared-client chats,
exact retries/reopen, commit/migration rollback, Unicode/NUL paging, held cleanup after
final output, persisted and live fences, and indexed recent-window selection over large
mixed histories. Tests use disposable SQLite and fake clients only; no browser/polling
performance claim. See the [read review map](../backend-development.md#read-contract-and-incremental-review-order).

This staging policy permits one input per active run and durably refuses a different busy
input; it does not implement the approved eventual mid-response delivery behavior. Stop/wait
helpers are internal, not a public hosting/shutdown API. Old nonterminal rows stay fenced
across reopen; startup recovery is not implemented. UI/HTTP, native-context fidelity, tools
and real-provider qualification remain outside this checkpoint. The owner requested notification
at tested milestones for their own file-by-file, line-by-line review before more layers accumulate.

## Existing constraints, not new decisions
- [Architecture](../../ARCHITECTURE.md): shared SQLite, message rows/bounded previews,
  WAL / `synchronous=NORMAL`. Returned commits survive process crash; uncommitted output
  may be lost. No power-loss, exactly-once-effect or attachment-file atomicity guarantee.
- [Session design](../../SESSION-DESIGN.md): small hierarchy/guarded ownership, not frozen
  APIs. [Execution design](../../EXECUTION-DESIGN.md): request-only client/Urd-owned cycle.
- ASP.NET-free Core; **Server → Core**. Server owns composition/HTTP/browser admission.
  [Application plan](../../APPLICATION-PLAN.md) owns component-led layout.
- Disconnect does not cancel accepted work; Stop waits for actual settlement. Unknown
  Send receipts reconcile, not replay. Original history remains readable.
- [SQLite validation](../../SQLITE-VALIDATION.md): coherent cursors, Unicode-safe previews,
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
public Task<SessionSummaryPage> ListPrimaryAsync(Guid? afterSessionId, ReadLimits limits);
public Task ShutdownAsync(CancellationToken waitCancellation);
```
Approved registry storage is flat and kind-neutral; `CreatePrimaryAsync`/`GetPrimaryAsync`
are typed primary entrypoints, not a primary-only registry. The registry gate covers
lookup/creation, never model execution. All following lifecycle mechanics remain proposed.
Startup versions storage/reconciles nonterminal runs before serving: **zero requests
restart**. Registry serialization guarantees one handle/ID; lookup never creates IDs.
Create/uncached lookup passes the single `CreatePrimary`/`ReadSession` result into the
handle constructor under the registry gate; cached lookup needs no hydration read.
NEW proposed list delegates to store without loading handles/history: immutable summary
page/items (ID, bounded preview, revision, run status), ordered by ID with next-ID cursor.
Each page is one read snapshot, not a cross-page snapshot; count, preview and byte caps
apply (defaults: 60 items, 256 UTF-16 units/preview, 64 KiB/page, surrogate-safe).
Shutdown fences registry access, then calls every handle's `FenceAdmissionAsync` before
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
private TaskCompletionSource<ExecutionResult>? _settlement;
private readonly Dictionary<Guid, (ViewLimits Limits, Channel<SessionView> Updates)> _observers;
protected SessionBase(SessionRecord record, SessionStore store, IChatClient chatClient);
public IAsyncEnumerable<SessionView> ObserveAsync(ViewLimits limits, CancellationToken observationCancellation);
public Task<HistoryPage> ReadHistoryAsync(HistoryCursor cursor, ReadLimits limits);
public Task<RowContentPage> ReadRowContentAsync(RowContentCursor cursor);
public Task<InputReceipt?> GetInputReceiptAsync(Guid inputId);
public Task<StopReceipt> RequestStopAsync(Guid runId);
public Task<ExecutionResult> WaitForSettlementAsync(Guid runId, CancellationToken waitCancellation);
protected Task<InputReceipt> AdmitInputAsync(InputCommand command);
internal Task FenceAdmissionAsync();
internal Task<LlmRequest?> ClaimNextRequestAsync(Guid runId);
internal Task<bool> RecordOutputAsync(Guid runId, LlmResponseChunk batch);
internal Task<bool> CompleteRequestAsync(Guid runId, LlmRequestOutcome completion);
private Task DriveExecutionAsync(SessionExecution execution);
private Task<bool> TrySettleAsync(Guid runId, ExecutionOutcome outcome);
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
Never await model work, settlement or subscribers under the guard. Once resources are
quiescent, `TrySettleAsync` rechecks pending input transactionally: normal outcome plus
more input continues the same run; otherwise commit settlement, clear execution and
complete `_settlement` under the guard. If that normal-outcome recheck finds pending input
after `RunAsync` returned, the single owner driver invokes and awaits `RunAsync` again on
the same retained execution/run, never concurrently or under the guard; release follows
terminal settlement only.
Stop/error terminalizes undelivered input honestly.
An ambiguous request/run terminal commit prevents continuation and keeps admission fenced
pending reconciliation; no false idle.
### 3. `PrimarySession : SessionBase` — ongoing primary input policy
Handle lifetime; no extra mutable fields. Chooses ordinary-input policy, not another lock.
```csharp
internal PrimarySession(SessionRecord record, SessionStore store, IChatClient chatClient);
public Task<InputReceipt> SubmitAsync(InputCommand command);
```
Input admitted idle starts a run. Busy preflight input is a **follow-up**, preserving
an answer to the first prompt; input after the initial request claim is **steering**,
consumed at the next safe model-response boundary, never by changing an in-flight request.
Follow-ups receive their own response; queued steering preserves acceptance order.
For this text-only checkpoint a safe boundary is a completed, recorded model response.
Stopping durably refuses new valid input before acceptance, with no run/message
creation, and leaves its draft client-owned; existing decisions remain readable.
No pending-edit, suffix-edit, snooze or final-close API is implied or removed here.

### 4. `SessionExecution` — fifth-approved temporary owned async work boundary
Owns requests, bounded output buffer and cancellation, not admission or transcript storage.
One per active run; no thread/process per session or global request queue. Constructor
initializes inert state only, never starts work. SessionBase creates/observes the sole
execution driver; no overlapping `RunAsync` calls or fire-and-forget cleanup.
```csharp
internal Guid RunId { get; }
private readonly SessionBase _owner;
private readonly IChatClient _chatClient; // borrowed
private readonly CancellationTokenSource _stop;
private readonly StringBuilder _outputBatch;
private int _batchUtf8Bytes;
private PeriodicTimer? _flushTimer; // activated during RunAsync, not construction
internal SessionExecution(Guid runId, SessionBase owner, IChatClient chatClient);
internal Task<ExecutionOutcome> RunAsync();
internal void RequestCancellation();
internal void ReleaseAfterSettlement();
```
A returned invocation has quiesced its model/enumerator, timer and append work; it is
not durable settlement. Owner rechecks pending input and either drives the same retained
object again, sequentially, or persists settlement. No direct store access; owner retains
claim/record/outcome/publication authority. The fifth approval covers the lifetime and
ownership boundary, not every SessionBase public contract or storage rule. Private pump,
reinvocation and timer mechanics are revisable implementation choices within that boundary;
substantive ownership, concurrency or persistence changes still need review.
Uses its Stop token, never HTTP/observer tokens. Claims input through owner, constructs
request-local MEAI messages and records through owner. Completion does not close chat.
A successful request requires positive terminal-success evidence (explicit in the fake
client), not clean enumeration, complete-looking text or absence of an exception; missing,
failed or incomplete terminal evidence cannot yield success. Each claimed request records
its own terminal outcome through `CompleteRequestAsync` before any continuation or normal
run settlement; output-item completion alone is not request success. Tools remain out of
this checkpoint.
Awaits owned cleanup before returning; noncooperation leaves Stop **stopping**, blocking
replacement. Cancellation/release are idempotent and safe against stale/racing calls.
Release owned resources only after invocation exit and confirmed durable settlement;
ambiguous settlement keeps admission fenced. Never dispose the borrowed client.

### 5. `SessionStore` — database lifetime; intent-specific transactions
Owns SQL/schema, serialized writer and short independent readers; no shared concurrent
connection or transaction across model work/observation. Calls are synchronous/bounded.
```csharp
private readonly SqliteConnection _writer;
private readonly object _writeGate;
private readonly string _connectionString;
private SessionStore(SqliteConnection writer);
public static SessionStore Open(string databasePath);
internal void InitializeAndRecover();
internal SessionRecord CreatePrimary(Guid sessionId);
internal SessionRecord? ReadSession(Guid sessionId);
internal SessionSummaryPage ListPrimary(Guid? afterSessionId, ReadLimits limits);
internal (InputReceipt Receipt, bool NewlyAccepted) AdmitInput(Guid sessionId, long historyRevision, InputCommand command);
internal InputReceipt? ReadInputReceipt(Guid sessionId, Guid inputId);
internal LlmRequest? ClaimNextRequest(Guid sessionId, Guid runId, long historyRevision);
internal WriteReceipt AppendOutput(Guid sessionId, Guid runId, long historyRevision, LlmResponseChunk batch);
internal WriteReceipt CompleteRequest(Guid sessionId, Guid runId, long historyRevision, LlmRequestOutcome completion);
internal StopReceipt RequestStop(Guid sessionId, Guid runId);
internal SettlementAttempt TrySettle(Guid sessionId, Guid runId, ExecutionOutcome outcome);
internal SessionView ReadView(Guid sessionId, ViewLimits limits);
internal HistoryPage ReadHistory(Guid sessionId, HistoryCursor cursor, ReadLimits limits);
internal RowContentPage ReadRowContent(Guid sessionId, RowContentCursor cursor);
internal ExecutionResult? ReadSettlement(Guid sessionId, Guid runId);
public void Dispose();
```
## Coordinated input → stream → Stop → recovery flow
`InputReceipt` correlates session/input ID and the exact original command (expected
history revision and bounded decoded text, without normalization). Its admission decision
is immutable **Accepted or Refused**: refusal has a reason and no admitted run; acceptance
has its original run/disposition and separately evolving request/terminal evidence.
Accepted never means model success, and later interruption never changes it to Refused.
`ExecutionResult` retains run ID and completed/cancelled/failed/interrupted outcome.
1. **Decide input — reliability contract approved; queue/schema details still proposed:**
   `InputCommand` carries
   stable input ID, expected history revision and text. For an existing session and valid
   command, one guarded store transaction resolves the ID or commits its first decision:
   - Existing ID with identical text **and original revision** returns retained evidence
     before today's capacity/revision/stopping checks. Conflicting reuse is an attempt
     error, never an overwrite or a refusal of the original command.
   - New acceptance commits input/receipt, steering/follow-up disposition and run association
     together (creating the active run only if idle). Only `NewlyAccepted`, not merely a
     returned Accepted receipt, may trigger new execution work. The flag is internal,
     per-call evidence, not part of the durable decision or public receipt.
   - New capacity/stale-revision/stopping refusal commits its receipt and matching command
     before returning. No accepted input/message/run or model work is created; a delayed
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
   `LlmRequest` carries request/run IDs, revision, input IDs and ordered model input,
   not UI state. `LlmRequestOutcome` carries that request ID and succeeded/failed/cancelled
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

   **Bounded row content:** immutable `RowContentCursor(RowId, HistoryRevision, Utf8Offset)`
   starts at offset zero using the stable row ID/revision from a view/history item.
   `RowContentPage` returns that identity/revision, `FromUtf8Offset`, decoded `Text`,
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
4. **Stop/settle:** `RequestStop(runId)` durably fences that run, cancels its undelivered
   inputs, then signals its token outside the guard. Receipt says stopping or retained
   settlement; stale/unknown run IDs never stop a newer run. Wait cancellation cancels
   only the wait. Settlement is persisted only after cleanup; no overlapping replacement.
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
