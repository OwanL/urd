# Initial UI / hosting design — scoped startup approval and remaining proposals
2026-11-05; startup, original five-operation HTTP and subsequent public Stop bites approved
2026-11-07. **These scoped hosting bites are implemented and verified.
The subsequent owner-approved saved-primary-ID slice is also implemented and verified.
The current surface has seven operations. UI and the broader checkpoint remain proposed.**

## Owner-approved vocabulary update

Current Core/HTTP names are user message submissions/acceptance and session executions;
the declarations below use this vocabulary without approving the remaining UI proposals.
The breaking rename has no legacy aliases. Saved IDs, revisions, SQL identifiers/schema 5,
decisions and exact-retry/Stop semantics are unchanged. The naming and Core structure cleanup
passed locked restore, **296/296 Release tests** and independent acceptance review; counts
below describe earlier checkpoints. See the
[current HTTP contract](../server-development.md#session-http-contract).

## Owner-approved first hosting bite — startup and shutdown

The owner approved this concrete operation with “go ahead” on 2026-11-07:
```csharp
internal static Task RunServerAsync(
  ServerOptions options,
  Action<Uri> onReady,
  CancellationToken stopping);
```

`Program` owns the operation; `ServerOptions` contains an explicit absolute data directory
and port (`0` requests an available port). One run owns a data-directory lease, SQLite
store, deterministic client, existing Core manager and IPv4-loopback Kestrel host.
`onReady` reports the actual bound origin only after SQLite and listening succeed.
The CLI writes one `urd-ready` JSON record to stdout and diagnostics to stderr.
Default environment/appsettings/URL configuration must not add listeners.

The only endpoint in this bite is bodyless `GET/HEAD /health`. Host/actual-port and
loopback-peer checks apply; any supplied Origin must exactly match the served origin.
No API marker, static assets, CORS grants, accounts, browser launch or chat routes yet.
On stop, close admission, initiate HTTP drain and Core session shutdown, and await both
without a forced timeout before disposing dependencies or releasing the data-directory
lease. Startup/readiness failures also clean up; a failed drain must not trigger forced
resource disposal. The run does not recover or replay orphan work.

The owner chose startup first rather than bundling a chat HTTP flow. This does not approve
new Core operations, transport/UI, real providers or the broader build wrapper proposal.
Implementation, commands and verified limits: [server development](../server-development.md).

## Owner-approved second hosting bite — five HTTP operations

The owner approved `SessionRoutes.MapSessions(app, sessionManager)` with “Proceed with the slice”,
while emphasizing that **queueing and steering are core functionality we must account for**.
This approval covers create, Send, receipt lookup, snapshot polling and segment-local content
paging over existing Core records. It does not make today's one-active-input refusal policy
permanent or approve a queue/steering implementation by implication.

The route group owns HTTP binding/validation and status translation, not a second session
registry or admission policy. It passes both Accepted and Refused receipts through as 200;
absence is literal JSON null, and generic errors never resolve an unknown Send. Only reads
use request cancellation; accepted execution survives disconnects. Conflicting-command reuse
has a typed Core exception, distinct from manager shutdown. Core schema/policy are unchanged.

Every API request requires `X-Urd-Client: browser` in addition to the local Host/Origin gate,
with no CORS grants. API responses are not cacheable. JSON bodies are bounded to 128 KiB and
decoded input to 16 KiB UTF-8. Wire records use camelCase properties and enum names, required
command members, GUID strings and integer revisions. Content uses six explicit query fields,
not the superseded global-byte cursor/SSE sketches below.

At this historical five-operation checkpoint, lost create acknowledgement remained unknown
and IDs could not be rediscovered without listing. The later listing slice below discovers
IDs but still cannot associate one with an unacknowledged create or justify automatic retry.
UI, listing, public Stop, startup recovery, providers and queue/steering were
not implemented by this bite. The owner accepted the current limits, not a finished chat workflow.
An internal client factory preserves server-owned construction/disposal while enabling causal
held-cleanup tests of the real host. Exact routes, commands and evidence are in
[server development](../server-development.md).

## Owner-approved third hosting bite — public addressed Stop

The owner approved public `SessionBase.RequestStopAsync(Guid executionId)` and the thin route
POST `/api/sessions/{sessionId}/executions/{executionId}/stop` with JSON `{}`. Independent review, locked
restore and **281/281 Release tests** pass without warnings, extending the steering checkpoint.
`StopReceipt(ExecutionId, Status, Outcome)` reports current status: 200 Requested (committed intent
and pending-steering retirement, not cleanup completion) or Settled (saved outcome), 404
NotFound including wrong-session runs, or 409 with Unavailable for an unowned unresolved run.
No orphan mutation/recovery or fake settlement; retries may advance Requested to Settled,
and a stale run ID cannot stop its replacement. Accepted input receipts remain unchanged.

The route retains local admission/no-store policy, JSON/body validation (400/415/413) and
503 shutdown/storage failures. It never passes `RequestAborted` into Core Stop or waits for
cancellation callbacks. The driver joins callbacks/LLM/tool cleanup before saved settlement;
existing snapshots show stopping/fencing and settlement. A failed Stop save still initiates
local cancellation and fences admission, but fails HTTP with 503, not a durable acknowledgement.
Core blocks late output/success and new work through local Stop guards; snapshot local intent
is observational, not proof of persistence. Actual tool results may still be saved.

No schema, new framework, UI, real tools or child runtime is added. Full bounded-descendant
requirements remain in [architecture](../ARCHITECTURE.md) and [session design](../SESSION-DESIGN.md);
separately opened independent worker chats continue. Stop does not undo effects or forcibly
kill arbitrary in-process functions. See the [Core approval record](initial-core-design.md#owner-approved-public-addressed-stop--2026-11-07)
for the owner-approved method/receipt and failure contract. Real HTTP tests exercise held
cleanup, unread acknowledgements, stale/independent runs, failed saves and unowned orphans.

## Owner-approved fourth hosting bite — saved-primary-ID discovery

Implemented and verified. GET `/api/sessions`, optionally
`?cursor=<opaque returned string>`, returns 200 `SessionIdPage` with camelCase `sessionIds`
and `nextCursor`: at most 60 IDs, newest-created first. It calls existing
`SessionManager.ListPrimaryIdsAsync(cursor, http.RequestAborted)` without opening handles,
loading history, starting execution, recovering work or changing persisted state. Empty and
retained unsettled chats are included. No summaries/titles, last-used tracking or UI is added.
Core's schema-5 metadata and atomic identity/order creation preserve existing evidence;
legacy rows use a fixed descending binary-ID fallback, not recovered creation chronology.
See the [Core approval](initial-core-design.md#owner-approved-saved-primary-id-discovery).

Omitted cursor starts the newest page; copy `nextCursor` unchanged until null. Core owns
parsing/formatting the opaque positive Int64 invariant-decimal string, not a JSON number.
Supplied empty/malformed/nonpositive/overflowing cursors return 400; valid exhausted cursors
return 200 with an empty page. Continuations use an ordinal range, so deleted boundary IDs
do not invalidate them. Pages see per-query committed state, not a frozen multi-page snapshot;
refresh page one for new chats. Existing loopback/Host/Origin/client-marker/no-store and
no-body GET rules apply; shutdown/storage failures remain 503. `RequestAborted` owns only
this read's guard wait/before-read check, not SQLite query interruption or session work.

Listing can rediscover IDs but cannot associate one with a lost create acknowledgement or
justify automatic create retry. IDs prove neither usability nor settled/stopped work; use
existing explicit detail reads. This narrow discovery supersedes only the old ID-order
summary/`ListPrimaryAsync` sketches below, not the UI or wider proposal. No full-history
navigation or automatic recovery follows. Independent review, locked restore and
**295/295 Release tests** pass without warnings; see the [server evidence](../server-development.md#verification-and-review).

## First browser-client bite — approved and implemented 2026-11-08

The owner approved the presented HTTP-only `SessionClient` class, its matching
TypeScript records, focused tests and minimal tooling. The class in
`source/ui/sessions/SessionClient.ts` implements `list`, `create`, `getSnapshot`,
`getMessageContent`, `submitMessage`, `getMessageReceipt` and `stopExecution` against
the current seven routes. Construction does no I/O; the class owns no polling,
selection, drafts, caches, IDs or execution state. Read cancellation does not stop
work, commands never retry automatically, and HTTP failures retain status/body
without fabricating admission decisions. Full original-submission matching remains
the future UI owner's responsibility.

This supersedes the old `Backend`/SSE transport sketch below for the first browser
increment, not the unapproved UI-state/component sketches. No changes to Core/schema/routes,
work on static assets/rendering/providers or larger scaffold are approved by this bite. The [browser development guide](../browser-development.md) owns tooling,
checks and evidence limits. New UI code uses Pie only as a visual/behavioural reference.

## Remaining UI / transport proposal

**Approved read-boundary update:** the owner approved Core snapshot/content reads and
snapshot polling for the first browser increment; see the sixth bite in the
[Core record](initial-core-design.md#sixth-bite--owner-approved-coherent-read-surface).
The earlier SSE/subscription/epoch-version and global-byte cursor sketches below are
superseded for that increment, not extra approved APIs. Use the actual snapshot plus
segment-local content cursor contract when implementing the browser; keep automatic
bounded follow beyond previews, coherent revisions and independent work lifetimes.
UI and additional hosting operations still require their scoped review.
Checkpoint: built Preact UI + ASP.NET server, two primary chats/two browsers,
deterministic request-only `IChatClient`, real SQLite, mid-response input, Stop,
history/reconnect/restart without replay. No providers/tools, LAN or desktop scaffold.
Existing: TypeScript/Preact, Core without ASP.NET, Server → Core; trusted-local UX
supersedes ACCESS-BOUNDARY-EVIDENCE's earlier credential/pairing recommendation.
[Browser](../BROWSER-DESIGN.md), [application](../APPLICATION-PLAN.md),
[session](../SESSION-DESIGN.md) and [Core draft](initial-core-design.md) retain scope.
Core members below remain provisional except the scoped approvals above; the Stop route/method
is now implemented. ID-only listing is separately approved above; the wider UI/SSE/summary
sketches remain unapproved and are not supplied by that discovery page.

## 1. `Workspace`, `Transcript`, `Composer` — ordinary Preact functions
One mounted workspace per browser; no frontend session-kind hierarchy or state library.
```tsx
function Workspace({ backend }: { backend: Backend }): JSX.Element;
function Transcript({ view, content, reader, onOlder, onReadContent, onReaderChange }: TranscriptProps): JSX.Element;
function Composer({ draft, pending, stopping, onChange, onSend, onRetryUnknown, onStop }: ComposerProps): JSX.Element;
```
`Workspace` composes chat navigation, explicit New chat, addressed connection/error
status, transcript and composer; it calls one hook below. An empty list shows New chat.
`Transcript` renders lazy content pages and owns DOM refs/selection measurements;
reader intent/anchor survives switching in the hook. It never starts/stops execution.
`Composer` renders Core dispositions (follow-up/steering, request outcome), not guessed
“delivered” ticks. Send stays usable during a response; stopping rejects before acceptance.
A submitted draft version clears only on confirmed acceptance; later typing survives.
Refused input leaves the current draft untouched, never restoring old text over later
edits. An unresolved submission has an explicit Retry original Send action, never an
automatic retry; explain that it may submit work that was not previously accepted.
No close/worker controls: independent-worker closure remains an unresolved later policy.

## 2. `useWorkspace(backend)` — per-browser navigation and bounded bookkeeping
```ts
type ChatId = string;
type Draft = { text: string; version: number };
type PendingSend = { submissionId: string; draftVersion: number; text: string;
  expectedRevision: number; status: 'sending' | 'unknown' };
type Reader = { follow: boolean; anchor?: { rowId: string; offsetPx: number } };
type CachedChat = { view: SessionView; older: HistoryPage[]; reader: Reader;
  content: Map<string, MessageContentPage[]>; freshness: 'stale' | 'fresh'; generation: number };
function useWorkspace(backend: Backend): {
  selectedId?: ChatId; summaries: SessionSummary[]; chats: Map<ChatId, CachedChat>;
  creation: 'ready' | 'creating' | 'unknown'; createChat(): Promise<void>;
  drafts: Map<ChatId, Draft>; pending: Map<ChatId, PendingSend[]>;
  select(id: ChatId): void; setDraft(id: ChatId, text: string): void;
  send(id: ChatId): Promise<void>; retryUnknown(id: ChatId, submissionId: string): Promise<void>;
  stop(id: ChatId, executionId: string): Promise<void>;
  loadOlder(id: ChatId): Promise<void>;
  loadMessageContent(id: ChatId, cursor: MessageContentCursor): Promise<void>;
  setReader(id: ChatId, reader: Reader): void;
};
```
Private effects own one selected-chat stream/AbortController, history/content-read controllers,
retry timer and generation counter; unmount releases these, not accepted backend work.
Mount lists chats, never creates automatically. `createChat` serializes explicit clicks;
confirmed ID refreshes summaries and selects it. Unknown outcome refreshes the list and
shows uncertainty, never guesses which ID is ours or retries creation automatically.
Selection is never sent to Server. A switch preserves the old draft/reader, shows cached
content as stale, refreshes summaries and opens a fresh observation after aborting the old.
Late responses address only their chat and generation; they never change selection.
Within a generation accept increasing view versions only. A new connection starts with
a fresh snapshot/epoch; never compare view versions across handle epochs/restarts.
Reconnect replaces the newest window and invalidates older pages on revision change;
original rows deduplicate by stable identity, never by text. No optimistic transcript rows.
Keep at most two chat caches, 120 rows/chat and 256 KiB decoded text total, including
lazy content pages/rendered text. Eviction preserves anchors or explicitly reports a
released window; no hidden uncapped body/DOM cache. Views/history retain Core's 60 rows,
256 UTF-16-unit surrogate-safe previews and 64 KiB cap; truncation opens lazy content.
`onReadContent` calls `loadMessageContent` → `backend.messageContent` → the one Core range read.
Start `MessageContentCursor(rowId, historyRevision, utf8Offset=0)`; pages carry identity,
revision, fromUtf8Offset/text/nextCursor/availableThroughUtf8Offset/hasMore/isFinal.
Each page is ≤16 KiB original UTF-8, scalar-safe; offsets are byte positions, not JS
string indices. Append preserves cursors; replacement rejects stale revision explicitly.
Load another page on reader demand, not the entire body. Abort/release content requests
and pages on generation/revision change; discard late mismatches, show preview until read.
An empty tail leaves nextCursor unchanged: never loop/poll it. Resume that cursor only
on reader demand after a newer view advertises appended bytes or changed row/run finality;
HasMore is snapshot-local, IsFinal alone says no future growth. Cache bounded page ranges,
key/upsert pages by row/revision/from-offset (empty tails are metadata, not extra pages).
Selected-chat follow mode counts as reader demand: growth/finality views trigger/coalesce
bounded reads for the visible response beyond its preview, with one in-flight read per
row and a rolling content/DOM window inside the existing caps. Paused/older-reading or
text selection requires explicit demand: no automatic prefetch, anchor yank or re-enabled
follow; label evicted gaps. An empty tail waits for new growth/finality evidence, not polling.
Paging prepends one bounded page, checks its revision, then evicts the opposite edge.
Streaming replaces the live newest window; it must not yank an older-reading anchor.
Follow only while at bottom, with no active text selection; “Jump to latest” is explicit.
Drafts and unresolved sends are separate from cache eviction. For this two-chat slice,
store them in tab-scoped `sessionStorage` before sending (bounded, versioned JSON); no
cross-client sync. Reload restores them; tab closure may lose unsent drafts. This is
not durable acceptance evidence. Max 16 KiB UTF-8/draft, eight unresolved sends/chat;
when full disable new Send with an explanation rather than evict unknown identities.
`retryUnknown` uses the saved command and existing submit endpoint: never changes ID,
text or expected revision, and never allocates another pending slot. It remains available
when new Send is disabled by that cap or stopping; serialize retries for the same ID.
Follow the [Core admission-decision contract](initial-core-design.md): clear an unresolved
slot only on a durable receipt matching session/submission ID, original text and revision.
Accepted clears only its submitted draft version; Refused leaves the current draft
untouched. Conflict, malformed response, generic 409/503, timeout or absence is not such
proof and keeps the original unknown. After confirmed refusal, a deliberate fresh Send
uses a new ID; it is not a retry of the refused command.
Reconnect only reads receipts; no mutation retries after missing receipt, EOF or timeout.
Restore and reconcile all saved unknown IDs without bypassing their matching checks.
Core's eight nonterminal accepted-input slots are separate from this unresolved UI cap;
refusal consumes neither accepted work capacity nor an unresolved slot once confirmed.

## 3. `Backend` — small HTTP client module, not a host framework
```ts
const backend: Backend = {
  list(cursor?: string, signal?: AbortSignal): Promise<SessionIdPage>,
  create(): Promise<{ sessionId: ChatId }>,
  history(id: ChatId, cursor: HistoryCursor, signal: AbortSignal): Promise<HistoryPage>,
  messageContent(id: ChatId, cursor: MessageContentCursor, signal: AbortSignal): Promise<MessageContentPage>,
  observe(id: ChatId, signal: AbortSignal): AsyncIterable<SessionView>,
  submit(id: ChatId, submission: UserMessageSubmission): Promise<UserMessageReceipt>,
  receipt(id: ChatId, submissionId: string, signal: AbortSignal): Promise<UserMessageReceipt | null>,
  stop(id: ChatId, executionId: string): Promise<StopReceipt>
};
```
Declaration sketch, not executable object syntax. `list` reflects the approved ID-only wire
contract; it supplies no `SessionSummary` data for the unapproved UI sketches above.
Wire records project Core data into
camelCase JSON with string GUIDs and integer revisions; test that boundary, no generic
DTO mapper/generated SDK. `SessionView` preserves epoch/version, history revision,
row identities/cursor, run ID/status, pending dispositions, committed output offsets and
nullable LastSettlement (retained terminal execution ID/outcome, distinct from a newer active execution).
Use **HTTP JSON + fetch-read SSE**, same origin. Every API fetch includes non-secret
`X-Urd-Client: browser`; no token/cookie, native EventSource or second transport.
`observe` decodes bounded UTF-8 SSE `event: view` / JSON `data`, including split chunks,
CRLF, comments and final partial frames. Max 256 KiB/frame/JSON response; overflow,
malformed data or EOF ends observation, marks stale and triggers bounded-backoff reopen.
Only read/observe/receipt GETs retry automatically; mutations never do. Release readers,
timers and abort listeners. No Last-Event-ID/event replay; every stream begins fresh.
Core's capacity-one replacement channel bounds queued views; Server has no update log.
One serialized writer awaits response writes, observes disconnect, and unregisters in
`finally`; slow/disconnected readers cannot hold Core guards or accumulate frames.
Unknown Stop acknowledgement shows unknown/stopping until a fresh authoritative view;
stream EOF cannot mean settled. Stop never derives its execution ID from a newer response.
Create is explicit, not an automatic retrying startup action: lost acknowledgement
refreshes the list and reports uncertainty, never silently creates another chat.

## 4. `Program` + `LocalBrowserGate` — process composition and browser admission
Suggested homes: `source/ui/{Workspace,Transcript,Composer}.tsx`, `use-workspace.ts`,
`backend.ts`; `source/hosting/server/{Program,LocalBrowserGate,SessionEndpoints}.cs`.
These are behavior files to introduce after approval, not folders/stubs to create now.
```csharp
record HostOptions(string DataRoot, int Port);
static Task<int> Main(string[] args);
sealed class LocalBrowserGate(RequestDelegate next, Func<Uri?> servedOrigin) {
    public Task InvokeAsync(HttpContext context);
}
```
`Program` validates `--data-root <absolute path> --port <1..65535 or 0>`; database path
is `Path.Combine(DataRoot, "urd.sqlite")`. Hosting owns exclusive data-root/database
ownership (a second host fails, not shares). Never infer data from cwd/UI output or
attach another listener. Build owns invocation/tool selection and runnable asset layout;
UI root is `AppContext.BaseDirectory/wwwroot`, staged with Server in
`builds/<Configuration>/app/`; no competing CLI flag. Serve built Vite assets, not source/data; no Node server or Vite proxy/CORS mode.
The approved startup composes one `SessionStore`, deterministic `IChatClient` and
`SessionManager`, opening SQLite before HTTP. `InitializeAsync`/startup recovery is still
an unimplemented proposal, not a dependency silently satisfied by opening the database. Bind `127.0.0.1` only; port 0 requests
an OS-assigned port, never wildcard/localhost fallback. Hosting resolves the actual
bound `http://127.0.0.1:<port>` origin, then writes one distinguished stdout JSON record:
`{"event":"urd-ready","origin":"http://127.0.0.1:<actual-port>"}`; logs use stderr.
Gate returns 503 until origin is set. Run/Smoke consumes readiness, never assumes a port.
Gate checks loopback peer and exact Host/actual port for every request; no forwarded
header trust. Public assets/health are bodyless GET/HEAD only, require no API marker,
and may omit Origin; they expose no private state. On all routes a supplied Origin
must equal the canonical served origin exactly; foreign and `null` reject. All `/api`
reads, mutations and streams require the marker even when Origin is absent. Reject
foreign preflights, with no CORS grants. POST requires JSON and bounded body (128 KiB); input text max 16 KiB
UTF-8 before Core admission. GET has no mutation effects. Reject before reading private
state or accepting commands. SPA fallback never masks unknown `/api` routes.
This blocks ordinary foreign browser access, not native/local programs (trusted).
No account, pairing, browser fingerprint, secret URL, cookie or LAN listener.
Server owns store/client disposal and follows [Core's caller-owned shutdown rule](initial-core-design.md):
gate new API admissions, abort/drain observations and admitted handlers/reads, and await
manager quiescence before disposal. Incomplete waits leave resources live as specified
there; request tokens never own accepted work.

## 5. `SessionEndpoints` — one thin route group and wire records
```csharp
static class SessionEndpoints {
    public static RouteGroupBuilder MapSessions(this IEndpointRouteBuilder app);
    private static Task StreamViewAsync(HttpContext http, PrimarySession session);
}
```
No endpoint base classes, command bus, per-route service or second session registry.
| Route under `/api` | Exact provisional Core dependency |
| --- | --- |
| GET `/sessions?cursor=...` (implemented) | `manager.ListPrimaryIdsAsync(cursor, http.RequestAborted)` → `SessionIdPage` |
| POST `/sessions` | `manager.CreatePrimaryAsync()`; return stable ID |
| GET `/sessions/{id}/history?cursor=...` | `GetPrimaryAsync(id)` → `ReadHistoryAsync(cursor, limits)` |
| GET `/sessions/{id}/content?cursor=...` | `GetPrimaryAsync(id)` → `ReadMessageContentAsync(cursor)` |
| GET `/sessions/{id}/view` (SSE) | `GetPrimaryAsync(id)` → `ObserveAsync(limits, http.RequestAborted)` |
| POST `/sessions/{id}/message-submissions` | `GetPrimaryAsync(id)` → `SubmitUserMessageAsync(submission)` |
| GET `/sessions/{id}/message-submissions/{submissionId}` | `GetPrimaryAsync(id)` → `GetUserMessageReceiptAsync(submissionId)` |
| POST `/sessions/{id}/executions/{executionId}/stop` | `GetPrimaryAsync(id)` → `RequestStopAsync(executionId)` |
The old observation-only cancellation sketch is superseded for implemented reads:
listing/receipt/snapshot/content use request cancellation, never execution cancellation.
Accepted Send/Stop continues after abort.
Stop returns admission/status promptly, not a request held until cleanup. Views show
settlement; no endpoint calls `WaitForSettlementAsync` merely to keep a browser alive.
Submit returns 200 with the matched durable `UserMessageReceipt` for **both** Accepted and
Refused; HTTP success means the admission decision was recorded, not that work ran.
Capacity/stale-revision/stopping reasons belong to that Refused receipt. Conflicting ID
reuse is a per-attempt 409, never a new decision for the original ID. Map malformed
requests/limits to 400/413, missing session/row to 404, other revision conflicts to 409,
and unavailable/ambiguous storage or pre-admission host refusal to 503.
Successful absent receipt is 200/null, never 404/503; it cannot prove an earlier Send
was rejected. Only a matched retained receipt resolves unknown admission. Wire errors
preserve identity but are not durable evidence. Core owns the decision transaction.

## Integrated contracts, two trade-offs and verification gate
- Core range reads, LastSettlement and exact input limits remain proposals; nullable
  receipt lookup and its unknown-absence semantics are part of the fourth approval.
  The revised durable Accepted/Refused contract plus explicit original-command retry
  addresses the unknown-Send gap. That user-Send behavior is now owner-approved in the
  Core record; UI/hosting APIs and implementation remain proposed, with production-path
  tests still required. JSON casing/cursor encoding do not need another API framework.
- Shared startup proposal: `--data-root <absolute path> --port <port or 0>`; Hosting
  validates/locks/recovers/reports actual origin, Build invokes/stages Server + `wwwroot`.
- Trade-off 1: HTTP + fetch SSE keeps commands/receipts simple; costs bounded framing
  and reconnect code. WebSocket avoids SSE parsing but adds request correlation; not both.
- Trade-off 2: selected-chat-only observation bounds readers and keeps navigation local;
  nonselected summaries refresh on selection/reconnect, not continuously across all chats.
After approval: built-browser/Kestrel/SQLite tests exercise A/B in two clients, drafts,
rapid switching/stale frames, lazy Unicode content paging/tail/revision/budget/anchor
checks, preflight follow-up and busy steering,
held-cleanup Stop, lost acknowledgements before/after admission decisions, refused retry
before delayed original, changed revision, mismatching receipts, retry at the full unresolved
cap, preserved later typing, and reload/restart without LLM/submission replay,
all clients detached while work continues, oversized frames and slow-reader disposal.
Acceptance: a followed response streams visibly beyond 256 units without clicks, under
rolling bounds/one read per row; pausing/selection stops automatic reads and preserves anchors.
Gate tests cover foreign/missing/null Origin, hostile Host/port/peer, missing marker,
foreign preflight and no-cors POST, read/stream disclosure; assert server effects too.
Recheck independent-navigation ≤100 incremental production LOC; no mirrored-mode option.
Outside the four scoped hosting bites above, this document remains design only. The linked server
development record owns the narrow build/test evidence, not the wider browser checkpoint.
Earlier reviews covered creation/browser admission, lazy content and follow behavior.
The later Astra critique found the unknown-Send gap and shutdown ownership drift addressed
in these revisions. Design review is not runtime verification or owner approval.
