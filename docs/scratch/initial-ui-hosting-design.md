# Initial UI / hosting design — owner review proposal
2026-11-05. **UI/hosting code shapes remain proposed.**

**Approved read-boundary update:** the owner approved Core snapshot/content reads and
snapshot polling for the first browser increment; see the sixth bite in the
[Core record](initial-core-design.md#sixth-bite--owner-approved-coherent-read-surface).
The earlier SSE/subscription/epoch-version and global-byte cursor sketches below are
superseded for that increment, not extra approved APIs. Use the actual snapshot plus
segment-local content cursor contract when implementing the browser; keep automatic
bounded follow beyond previews, coherent revisions and independent work lifetimes.
Other UI/hosting operations and implementation still require their scoped review.
Checkpoint: built Preact UI + ASP.NET server, two primary chats/two browsers,
deterministic request-only `IChatClient`, real SQLite, mid-response input, Stop,
history/reconnect/restart without replay. No providers/tools, LAN or desktop scaffold.
Existing: TypeScript/Preact, Core without ASP.NET, Server → Core; trusted-local UX
supersedes ACCESS-BOUNDARY-EVIDENCE's earlier credential/pairing recommendation.
[Browser](../BROWSER-DESIGN.md), [application](../APPLICATION-PLAN.md),
[session](../SESSION-DESIGN.md) and [Core draft](initial-core-design.md) retain scope.
Core members below are provisional dependencies, **not newly approved operations**.

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
type PendingSend = { inputId: string; draftVersion: number; text: string;
  expectedRevision: number; status: 'sending' | 'unknown' };
type Reader = { follow: boolean; anchor?: { rowId: string; offsetPx: number } };
type CachedChat = { view: SessionView; older: HistoryPage[]; reader: Reader;
  content: Map<string, MessageContentPage[]>; freshness: 'stale' | 'fresh'; generation: number };
function useWorkspace(backend: Backend): {
  selectedId?: ChatId; summaries: SessionSummary[]; chats: Map<ChatId, CachedChat>;
  creation: 'ready' | 'creating' | 'unknown'; createChat(): Promise<void>;
  drafts: Map<ChatId, Draft>; pending: Map<ChatId, PendingSend[]>;
  select(id: ChatId): void; setDraft(id: ChatId, text: string): void;
  send(id: ChatId): Promise<void>; retryUnknown(id: ChatId, inputId: string): Promise<void>;
  stop(id: ChatId, runId: string): Promise<void>;
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
slot only on a durable receipt matching session/input ID, original text and revision.
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
  list(after?: string, signal?: AbortSignal): Promise<SessionSummaryPage>,
  create(): Promise<{ sessionId: ChatId }>,
  history(id: ChatId, cursor: HistoryCursor, signal: AbortSignal): Promise<HistoryPage>,
  messageContent(id: ChatId, cursor: MessageContentCursor, signal: AbortSignal): Promise<MessageContentPage>,
  observe(id: ChatId, signal: AbortSignal): AsyncIterable<SessionView>,
  submit(id: ChatId, command: InputCommand): Promise<InputReceipt>,
  receipt(id: ChatId, inputId: string, signal: AbortSignal): Promise<InputReceipt | null>,
  stop(id: ChatId, runId: string): Promise<StopReceipt>
};
```
Declaration sketch, not executable object syntax. Wire records project Core data into
camelCase JSON with string GUIDs and integer revisions; test that boundary, no generic
DTO mapper/generated SDK. `SessionView` preserves epoch/version, history revision,
row identities/cursor, run ID/status, pending dispositions, committed output offsets and
nullable LastSettlement (retained terminal run ID/outcome, distinct from a newer active run).
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
stream EOF cannot mean settled. Stop never derives its run ID from a newer response.
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
Compose one `SessionStore`, deterministic `IChatClient`, `SessionManager`; await
`InitializeAsync`/recovery before opening HTTP. Bind `127.0.0.1` only; port 0 requests
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
| GET `/sessions?after=...` | `manager.ListPrimaryAsync(after, limits)` |
| POST `/sessions` | `manager.CreatePrimaryAsync()`; return stable ID |
| GET `/sessions/{id}/history?cursor=...` | `GetPrimaryAsync(id)` → `ReadHistoryAsync(cursor, limits)` |
| GET `/sessions/{id}/content?cursor=...` | `GetPrimaryAsync(id)` → `ReadMessageContentAsync(cursor)` |
| GET `/sessions/{id}/view` (SSE) | `GetPrimaryAsync(id)` → `ObserveAsync(limits, http.RequestAborted)` |
| POST `/sessions/{id}/inputs` | `GetPrimaryAsync(id)` → `SubmitUserInputAsync(command)` |
| GET `/sessions/{id}/inputs/{inputId}` | `GetPrimaryAsync(id)` → `GetInputReceiptAsync(inputId)` |
| POST `/sessions/{id}/runs/{runId}/stop` | `GetPrimaryAsync(id)` → `RequestStopAsync(runId)` |
Only observation gets HTTP cancellation. Existing uncancellable Core reads finish
boundedly; HTTP abort discards their replies. Accepted Send/Stop continues after abort.
Stop returns admission/status promptly, not a request held until cleanup. Views show
settlement; no endpoint calls `WaitForSettlementAsync` merely to keep a browser alive.
Submit returns 200 with the matched durable `InputReceipt` for **both** Accepted and
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
cap, preserved later typing, and reload/restart without model/command replay,
all clients detached while work continues, oversized frames and slow-reader disposal.
Acceptance: a followed response streams visibly beyond 256 units without clicks, under
rolling bounds/one read per row; pausing/selection stops automatic reads and preserves anchors.
Gate tests cover foreign/missing/null Origin, hostile Host/port/peer, missing marker,
foreign preflight and no-cors POST, read/stream disclosure; assert server effects too.
Recheck independent-navigation ≤100 incremental production LOC; no mirrored-mode option.
This document is design only: no build, probe, package acquisition or runtime claim.
Earlier reviews covered creation/browser admission, lazy content and follow behavior.
The later Astra critique found the unknown-Send gap and shutdown ownership drift addressed
in these revisions. Design review is not runtime verification or owner approval.
