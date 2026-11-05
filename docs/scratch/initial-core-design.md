# Initial Core class design — owner review proposal
2026-11-05. **NEW proposed API/state shape; not approved and not implementation permission.**
Checkpoint only: Windows local browser app, deterministic request-only `IChatClient`,
two primary chats/two clients, real SQLite, mid-response input, addressed Stop/settlement
and restart/reconnect without replay. No code, builds, schema or transport approval.
The Core workstream owns this draft only; implementation remains gated on owner review.

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
## Five substantial types: responsibility, lifetime, members
Fragments are declarations only, not compilable definitions. Named command/results are
immutable data, not behavioral classes. `Guid` identifies session/run/input/output;
`long` history revision fences replacement; `long` view version orders live views.
No future-kind, tool, provider/login/context or assignment classes or inheritance hooks.

### 1. `SessionManager` — one registry per backend/database lifetime
Owns canonical handles, recovery and shutdown fencing, not the loop. Handles remain
registered until shutdown for this two-chat checkpoint; no eviction scheme.
```csharp
private readonly SessionStore _store;
private readonly IChatClient _chatClient;
private readonly SemaphoreSlim _registryGate;
private readonly Dictionary<Guid, PrimarySession> _sessions;
private bool _accepting;
public SessionManager(SessionStore store, IChatClient chatClient);
public Task InitializeAsync();
public Task<PrimarySession> CreatePrimaryAsync();
public Task<PrimarySession> GetPrimaryAsync(Guid sessionId);
public Task<SessionSummaryPage> ListPrimaryAsync(Guid? afterSessionId, ReadLimits limits);
public Task ShutdownAsync(CancellationToken waitCancellation);
```
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
Cancelled shutdown waits leave resources live. Server first gates/drains external queries
and subscriptions; manager closes storage only after settlement. Server then disposes its
configured client. Exclusive database ownership is a host precondition, not a lease API.

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
private TaskCompletionSource<RunSettlement>? _settlement;
private readonly Dictionary<Guid, (ViewLimits Limits, Channel<SessionView> Updates)> _observers;
protected SessionBase(SessionRecord record, SessionStore store, IChatClient chatClient);
public IAsyncEnumerable<SessionView> ObserveAsync(ViewLimits limits, CancellationToken observationCancellation);
public Task<HistoryPage> ReadHistoryAsync(HistoryCursor cursor, ReadLimits limits);
public Task<RowContentPage> ReadRowContentAsync(RowContentCursor cursor);
public Task<InputReceipt?> GetInputReceiptAsync(Guid inputId);
public Task<StopReceipt> RequestStopAsync(Guid runId);
public Task<RunSettlement> WaitForSettlementAsync(Guid runId, CancellationToken waitCancellation);
protected Task<InputReceipt> AdmitInputAsync(InputCommand command);
internal Task FenceAdmissionAsync();
internal Task<RequestPlan?> ClaimNextRequestAsync(Guid runId);
internal Task<bool> RecordOutputAsync(Guid runId, OutputBatch batch);
internal Task<bool> CompleteRequestAsync(Guid runId, RequestCompletion completion);
private Task DriveExecutionAsync(SessionExecution execution);
private Task<bool> TrySettleAsync(Guid runId, ExecutionOutcome outcome);
private SessionView CaptureView(ViewLimits limits);
```
NEW proposed hydration: constructors copy ID/revision from the stored `SessionRecord`,
with no I/O or background work. Post-recovery handles start without execution/settlement
waiter and with fresh view epoch/version; retained run/settlement facts remain store-readable.
Only the handle mutates session/run state under `_gate`; SQL repeats ID/revision/lifecycle
checks. Admission also checks `_accepting`; shutdown fences it under that same guard.
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
Stopping rejects new input before acceptance and leaves its draft client-owned.
No pending-edit, suffix-edit, snooze or final-close API is implied or removed here.

### 4. `SessionExecution` — temporary owned async work for one run
Owns requests, bounded output buffer and cancellation, not admission. One per active
run; no thread/process per session or global request queue.
```csharp
public Guid RunId { get; }
private readonly SessionBase _owner;
private readonly IChatClient _chatClient;
private readonly CancellationTokenSource _stop;
private readonly StringBuilder _outputBatch;
internal SessionExecution(Guid runId, SessionBase owner, IChatClient chatClient);
internal Task<ExecutionOutcome> RunAsync();
internal void RequestCancellation();
internal void ReleaseAfterSettlement();
```
Uses its Stop token, never HTTP/observer tokens. Claims input through owner, constructs
request-local MEAI messages and records through owner. Completion does not close chat.
A successful request requires positive terminal-success evidence (explicit in the fake
client), not clean enumeration, complete-looking text or absence of an exception; missing,
failed or incomplete terminal evidence cannot yield success. Each claimed request records
its own terminal outcome through `CompleteRequestAsync` before any continuation or normal
run settlement; output-item completion alone is not request success. Tools remain out of
this checkpoint.
Awaits owned cleanup before returning; noncooperation leaves Stop **stopping**, blocking
replacement. Cancellation/release are idempotent; release resources only after settlement.

### 5. `SessionStore` — database lifetime; intent-specific transactions
Owns SQL/schema, serialized writer and short independent readers; no shared concurrent
connection or transaction across model work/observation. Calls are synchronous/bounded.
```csharp
private readonly SqliteConnection _writer;
private readonly object _writeGate;
private readonly string _connectionString;
public SessionStore(string databasePath);
internal void InitializeAndRecover();
internal SessionRecord CreatePrimary(Guid sessionId);
internal SessionRecord ReadSession(Guid sessionId);
internal SessionSummaryPage ListPrimary(Guid? afterSessionId, ReadLimits limits);
internal InputReceipt AdmitInput(Guid sessionId, long historyRevision, InputCommand command);
internal InputReceipt? ReadInputReceipt(Guid sessionId, Guid inputId);
internal RequestPlan? ClaimNextRequest(Guid sessionId, Guid runId, long historyRevision);
internal WriteReceipt AppendOutput(Guid sessionId, Guid runId, long historyRevision, OutputBatch batch);
internal WriteReceipt CompleteRequest(Guid sessionId, Guid runId, long historyRevision, RequestCompletion completion);
internal StopReceipt RequestStop(Guid sessionId, Guid runId);
internal SettlementAttempt TrySettle(Guid sessionId, Guid runId, ExecutionOutcome outcome);
internal SessionView ReadView(Guid sessionId, ViewLimits limits);
internal HistoryPage ReadHistory(Guid sessionId, HistoryCursor cursor, ReadLimits limits);
internal RowContentPage ReadRowContent(Guid sessionId, RowContentCursor cursor);
internal RunSettlement? ReadSettlement(Guid sessionId, Guid runId);
internal void Dispose();
```
## Coordinated input → stream → Stop → recovery flow
`InputReceipt`: input/run IDs, disposition, request/terminal status. `RunSettlement`:
run ID and completed/cancelled/failed/interrupted outcome, not merely a cancelled token.
1. **Accept:** `InputCommand` carries stable input ID, expected history revision and text.
   Under the handle guard, admission atomically stores input/receipt, its steering or
   follow-up disposition and run association (creating the active run only if idle).
   Same ID/same content returns retained evidence; conflicting reuse rejects. Existing-ID
   resolution precedes new-work capacity, revision and busy rejection and remains usable
   while busy. Core enforces at most **16 KiB UTF-8/input** and **eight nonterminal accepted
   inputs/session**, including claimed inputs awaiting request outcome. Admission checks
   these under the guard and repeats the capacity check in its transaction. Oversize or
   capacity rejects explicitly before acceptance, with no input/run write; the client keeps
   its draft. These are engineering defaults, not transport-only validation or history caps.
   Receipt lookup returns retained evidence or `null` after a successful snapshot proves
   absence there. Missing session is distinct; unavailable/uncertain storage never returns
   `null` as a fallback. Absence is not rejection evidence for an earlier still-in-flight
   Send, nor authority to replay it. Lost acknowledgement: retain that ID and query;
   unavailable/ambiguous storage means unknown, never rejection.
2. **Deliver/record:** request claim atomically records which queued inputs belong to
   that request. Claiming is not proof of provider receipt/consumption or a completed
   answer; the receipt must retain the request outcome rather than imply success.
   `RequestPlan` carries request/run IDs, revision, input IDs and ordered model input,
   not UI state. `RequestCompletion` carries that request ID and succeeded/failed/cancelled
   outcome; success requires the validated positive terminal evidence above, even for an
   empty response. Its guarded transaction checks request/run/revision and commits the
   request outcome plus associated input receipts independently of run settlement. Identical
   completion returns retained evidence; conflicting/stale completion rejects. A stopping
   run cannot newly record success, but may record failed/cancelled cleanup outcomes.
   NEW simplicity proposal: commit each bounded fake output chunk **before** publication;
   no provisional UI tail for this checkpoint. `OutputBatch` has request/output IDs, batch
   index, text and output-item final marker; append checks request/run/revision/stopping
   and expected next index. Slice oversized chunks; backpressure enumeration on writes.
   No per-token fsync promise.
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
   committed request outcomes/receipts, marks prior active runs and nonterminal requests,
   partial output and undelivered accepted input interrupted, retaining receipt identities
   and committed order; none is automatically redelivered. Reconnect
   queries unresolved receipts before any deliberate new Send. This does not decide future
   due-never-dispatched follow-ups or authorize replay of missing external-effect results.

## Owner review gates / limits
- NEW choices: five responsibilities/members, guarded driver/transaction split, same-run
  queued input, commit-before-publish and bounded replacement views. Not approved.
- Eviction/context/view costs need evidence; UI caps are not context caps/latency targets.
  Transport/browser gate/wire DTOs need a separate pack; navigation retains its ≤100 LOC gate.
- Unconfirmed cleanup/DB outcomes stay visible. Providers/tools, edits/closure and their
  uncertainty exceed this pack: flag critical complexity before expanding APIs.
