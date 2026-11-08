# Transcript stage 1 — history, live updates and placement foundation

**Status: concrete proposal for review, not implementation approval.**
Source checked at `5c9392f` on 2026-11-09. Requirements are owned by
[Transcript design](TRANSCRIPT-DESIGN.md); parsing evidence is separate.
No production code, dependency, migration or running-application change is made here.
Revised 2026-11-09 after checking the plan against the overall requirements
(low overhead, snappy streaming, tool rows, images, Markdown, animation), then again
after the second requirements pass (design decisions 19–30). Revised 2026-11-09 after
review against the wider requirements: usage capture is reassigned to the harness
(execution/storage, stored per request) with no stage-1 usage read contract, and cost
estimation is deferred until a pricing source exists. UI decisions remain open; this
plan covers backend contracts and data the future UI needs.

## Outcome and explicit boundary

Build the foundation later stages render on:
1. a history index whose unit is a **transcript entry** (user message, assistant
   text run, tool call) in the order the model produced it, addressable without
   loading bodies, carrying times, tool start/end and per-reply usage;
2. bounded anchor-based reads of that index;
3. **push change notices** over one stream per browser page, replacing 350 ms
   snapshot polling;
4. input placed in the conversation when a send attempt starts;
5. the 16 KiB caps on tool arguments, tool results and user submission text
   removed (owner: "just lift them, we can add it back if we need to"). No
   replacement ceiling is introduced; reads of these values stay bounded by the
   readers that serve them, not by write-time caps. The 16 KiB output segment and
   content-page sizes are chunking, not caps, and stay; provider-context and API-key
   limits are unrelated and unchanged.

Urd is not yet used for real work; prefer the right foundation over preserving
interim UI behavior, but never weaken receipts, Stop, failure or uncertainty rules.

Not included: Markdown parsing, DOM virtualization, rendered-height estimation,
whole-history scrollbar positions, tool input/output inspection, tool-draft
streaming, reasoning, attachments, search, custom event framework. Keep current
text content reads and their UTF-8 cursors.

## Review corrections applied

- **Wrong index unit.** The first draft indexed only user/assistant text. Tool calls
  live in `execution_tools`, outside the index; a tool-only response is just an empty
  assistant entry. A tool-heavy session (hundreds of calls) could not anchor, window
  or bound its tool rows, and stage 3 would have to rebuild identities/order. The
  index now holds kind-tagged entries, and empty assistant entries need not render.
- **Rank/count dropped from stage 1.** A dense message rank is not scroll geometry;
  stage 2 must build a weighted position index anyway. Building both means two
  migrations and per-write summary maintenance for little benefit. Positional lookup
  moves to stage 2, designed once with its real weights.
- **Persisted observation version dropped.** Updating a summary row on every streamed
  chunk adds write cost on the hottest path. Change tracking becomes an in-memory
  per-session counter feeding push notices (section 3).
- **Polling replaced.** Up to 350 ms display lag plus a 60-preview reread per poll
  contradicts snappy streaming and low overhead; tool-input streaming would inherit it.

## 1. Entry identity and order

- Extend the existing history index (`execution_messages`, renamed only if clarity
  warrants the churn) to hold **kind-tagged entries**: `user`, `text` (assistant text
  run) and `tool`. Later kinds join without changing anchors: `reasoning` (stage 3,
  decision 20), compaction marker (decision 22), subagent card (decision 21) and
  attachment references on user entries (stage 4).
- **Observed order (decision 19).** One reply may contain several text runs and
  calls, e.g. text → call → text → call. A text run owns a contiguous range of its
  request's output segments; a call boundary flushes and closes the current run, and
  the next text opens a new run entry. Content cursors become per text run, mapped
  onto the request's existing segment numbering (a run is a `batch_index` range);
  batch numbering does not restart per run, so model-context reconstruction is
  unchanged. Where an
  adapter does not expose item order (today calls are collected at completion),
  record text-then-calls; never fabricate interleaving. Adapter ordering support
  is a separate provider task, not a stage 1 requirement.
- Entry identity is a stable reference: execution, kind, entry ID, history revision.
  `MessageRef` stays valid for text entries; tool entries reference
  `(request, call ID)` and carry a GUID entry ID like the others. Do not overload
  request, execution, submission and entry identities.
- Order is the index's insertion order (`ordinal`), scoped by session/revision
  through the existing `(session_id, history_revision, ordinal)` index. Global
  ordinal gaps are fine for keyset reads; they are not ranks and never displayed.
- Insertion points: user entries at send-attempt start (section 4); text-run entry
  on its first saved segment; a tool entry is **reserved when its call boundary is
  observed**: the writer flushes and closes the current text run, then inserts the
  tool entry (call ID and name if known, state `observed`, no arguments, no execution
  authority) before any later text commits. Completion attaches the validated call;
  a reply that fails, is cancelled or never validates the call leaves the entry
  closed as not executed. Calls only reported at completion (today's adapters) are
  reserved then, in `call_index` order, giving text-then-calls. Stage 3's streamed
  drafts reuse the same reserved entry; no second allocation path.
- **Entry facts (decision 27):** creation time on every entry; tool start and end
  times. Token usage per model reply is a **harness-owned capture**, not a
  transcript entry fact: the request row stores usage from provider `UsageContent`
  (which `LlmResponseAccumulator` currently discards) so the harness's post-closure
  usage summaries and later transcript detail views have data. Stage 1 adds no usage
  read contract; future detail views read it on demand by request ID. Cost
  estimation is not captured: no pricing source exists, so cost stays unknown until
  one does. Missing usage stays unknown, never approximated.
- **Edits (decision 23):** an edit permanently discards later entries; their rows
  and owned content are deleted, not hidden. A revision change invalidates anchors;
  the client falls back to the edited prompt. No production edit exists yet; stage 1
  only keeps revision fencing correct.
- Migration backfills tool entries for existing saved calls after their request's
  text-run entry (or in request order when none exists), preserving existing
  message order. Ordinals are append-only, so the migration rebuilds the index in
  the merged order rather than appending tool rows at the tail. Do not reorder
  legacy user rows.

## 2. Bounded history reads

Proposed route: `GET /api/sessions/{sessionId}/history`, one reader with modes:

| Mode | Input | Result |
| --- | --- | --- |
| `latest` | Expected revision | Newest window, oldest first |
| `before` / `after` | Exact entry reference + expected revision | Strict neighbors, oldest first |
| `around` | Exact entry reference + expected revision | Window including the anchor |

- Initial window ceiling: reuse the existing 60-entry convention as a **metadata page
  size**, not a memory/cache budget. Refill from the other side near a boundary.
- Descriptors carry no bodies or previews: entry reference, kind, lifecycle facts.
  Text: committed extent (`AvailableThrough`), closed, final-output marker, time.
  Tool: name, started/outcome, start/end times. Execution settlement is reported
  once per page, not repeated per entry; per-request usage is not part of the page
  contract (harness-owned, served by a later bounded read when detail views need
  it). Arguments/results stay out (stage 3 inspection). An entry's full original source remains readable through
  bounded content pages (future copy/export needs no new contract).
- One `Database.Read` transaction per page; `hasBefore`/`hasAfter` via indexed
  lookahead. Stale revision → existing 409, checked before anchor lookup; missing or
  wrong-session anchor → not found. No server-side fallback to latest.
- Appends do not change history revision; pages are individual observations merged
  by entry identity, not a frozen paging snapshot.
- Snapshot stops carrying 60 previews once the transcript reads history; keep it to
  session/execution status, pending input, and **one short preview of the first
  user entry** for session naming. `WorkspaceCommands` currently names discovered
  sessions from `snapshot.messages`; packet 1B migrates that consumer to the new
  field in the same change, with a discovered-session regression. Removing the
  other 59 preview reads removes most of the per-update body decoding.

## 3. Push change notices

- **One stream per browser page**, multiplexed by session: `GET /api/events`,
  consumed with `fetch` + a stream reader, so the existing `X-Urd-Client` header
  gate is kept (`EventSource` cannot send it). Server-Sent Events framing is
  sufficient; no WebSocket, no new dependency. Per-session streams are rejected:
  browsers allow about six HTTP/1.1 connections per host, and a parent plus
  streaming subagent cards would exhaust them and stall ordinary reads.
- The page declares its **open sessions** (its tabs) and, within them, its
  **watched sessions** (visible conversation, expanded or streaming subagent cards)
  through a small request bound to its stream; changing either set does not
  reconnect. For every open session the stream carries its small status itself
  (running/idle/failed/fenced/needs attention), not just a notice, so tabs stay
  truthful without content reads. Unwatched sessions cost no content notices.
- **Status has the same no-lost-update rule:** registering an open session (on
  connect, reconnect or opening a tab) reads its status, including in-memory
  fencing, under the session gate and sends it as that session's first status event;
  every later transition sends the new status. Test a settlement and a fencing
  transition racing registration of an unwatched session with no later change.
- Content notices are tiny and coalescing: `{ session, boot, version }` plus
  optionally the changed entry kinds. No content, no event log, no replay. The
  client reads what is on screen (history window tail, open content) — at most one
  read in flight per session and kind, newest notice wins.
- `SessionBase` owns all session writes, so it bumps an **in-memory** counter after
  each change that affects reads: committed changes (entry insert, output append,
  tool state, settlement, pending input, Stop) **and in-memory state changes the
  snapshot exposes**, such as admission fencing after an unexpected execution
  failure or a failed store write. Bump the counter where the state changes, not
  per storage call, so no observable transition is missed. `boot` identifies the
  process so restarts force a full resync. Nothing is persisted.
- Writers never wait for subscribers: a slow or vanished client only misses
  intermediate versions. Bound watchers per page and pages per process; drop on
  disconnect.
- On connect/reconnect, open sessions receive fresh status events and watched
  sessions repeat the handshake below, then read their snapshot + visible history.
  For watched content the stream only says "something changed". A hidden page holds
  no stream; focus/visibility reconnects.
- **No lost final update — subscribe first, then read.** When a session is added to
  the watch set, the server registers it and reads its current version under the
  session gate, then sends that as the session's first event. The client starts
  that session's reads only after the first event. Any commit after registration therefore produces a later notice; any
  commit before it is covered by the initial reads. If a notice arrives while a read
  is in flight, schedule exactly one more read after it completes rather than
  dropping the notice. A periodic heartbeat detects a dead connection; reconnect
  repeats this handshake. Test a settlement committed during connection setup.
- Live latency is then bounded by output commit cadence (currently a 100 ms flush
  interval in `LlmResponseWriter`), not the 350 ms poll. Measure
  commit → notice → rendered delay on a local fixture; if commit batching dominates,
  that is the next thing to tune, not a reason to send content in notices.

## 4. Conversation placement at a send attempt

Unchanged from the reviewed draft in substance:

- After client/options preparation, immediately before
  `IChatClient.GetStreamingResponseAsync`, an awaited `StartRequestAsync` marks the
  request attempted and inserts its assigned inputs as user entries, in admission
  order, in one transaction. Idempotent; joins the session gate; refuses after Stop.
- Request attempt fact: `not attempted` / `attempted` / legacy `unknown`. An attempt
  is not proof of delivery; crash between commit and network call stays uncertain.
- Failure after the hook keeps the message with the failed attempt; no requeue.
  Save failure of the marker escapes as execution uncertainty, never as an ordinary
  provider error, and the provider is not called.
- `LlmContextReader` excludes prior requests known never attempted; keeps the
  current batch and legacy history.
- Pending inputs appear in the snapshot as identity plus a bounded preview (the
  existing 256-UTF-16-unit convention), never full text: submission text is
  uncapped after packet 1A, and the snapshot is a hot path. Full pending text
  stays readable through the existing receipt route. The outstanding count cap
  still bounds the pending list itself.
  Terminal never-attempted inputs are **not sent**: discoverable through a sparse,
  keyset-paged session listing; text read through the existing receipt route;
  explicit resend uses a new submission ID; drafts are never overwritten.

Promotion from pending to conversation is a visible transition. Backend facts must
let the client append it without moving content the user is reading; presentation
and animation are left to the UI design.

## 5. Existing data

Schema v8 has no attempt fact. Preserve existing visible rows and order, mark
attempt provenance unknown, backfill tool entries as above. Schema v8 also has no
entry times, tool start/end times or usage: legacy values stay **unknown** (null),
never approximated. Existing reply text becomes one text run per request. No guessed
reordering, deletion or replay. Genuine old-schema fixture tests upgrade/rollback and
prove no invented times, usage or attempt provenance. No live
`C:/dev/data/urd` migration is authorized by this plan.

## 6. Implementation packets

Sequential; they share storage/session files.

**1A — entry index and history reads.** Migration (entry kinds, text runs, entry
facts, tool backfill, lifted 16 KiB caps on tool arguments/results and submission
text, including `LlmResponseAccumulator`, provider stream and route/client checks),
harness-owned usage capture (stored per request, no read route), history reader,
core method, route, typed client. Old user placement unchanged.

**1B — push notices.** In-memory session version, page stream with watch set and
open-session status, client stream reader replacing `useSessionSnapshot` polling
(now called from `app/Conversation.tsx`); snapshot slimmed once the transcript reads
history. Keep the existing rendered regressions passing.

**1C — send-attempt placement.** Attempt fact/hook, context eligibility, pending and
not-sent projection with minimal client presentation, promotion without anchor
movement.

Likely owning files: backend `core/sessions/` (`Messages.cs`, `SessionSnapshot.cs`,
`SessionBase.cs`), `core/storage/` (`DatabaseSchema.cs`, `SnapshotReader.cs`,
`MessageContentReader.cs`, `UserMessageStorage.cs`, `SessionExecutionStorage.cs`,
`LlmResponseStorage.cs`, `LlmContextReader.cs`), `core/execution/SessionExecution.cs`,
`LlmResponseAccumulator.cs`, `LlmResponseWriter.cs`, a new focused history reader;
`source/hosting/server/SessionRoutes.cs` plus a page-stream route; UI
`sessions/Messages.ts`, `SessionClient.ts`, `app/useSessionSnapshot.ts`,
`app/Conversation.tsx` and the existing workspace/transcript owners after
coordination with the frontend audit session.

## 7. Acceptance checks

Reuse `SessionSnapshotStorageTests` seeding/poisoned-body/query-plan helpers,
`MessageContentTests`, `ExecutionStorageTests`, `SteeringFlowTests`,
`SessionStopTests`, `SessionExecutionTests`, `SessionRoutesTests`,
`SessionClient.test.ts` and the offline rendered workspace regression.

- Reads: all modes, boundaries, empty history, wrong-session anchor, stale revision,
  mixed kinds including a tool-only request, interleaved sessions; poisoned bodies
  prove no text/argument decoding; indexed plans without OFFSET/COUNT scans.
- Entries: with a test adapter that reports call boundaries, a reply text → call →
  text yields three entries in that order; with today's completion-only adapters,
  text-then-calls; a reserved call whose reply fails stays closed as not executed and
  never runs; per-run content reads
  stay cursor-correct; entry times and tool start/end are saved; per-request usage
  is captured on the request row with missing usage left unknown and no usage read
  added.
- Push: notice after each change kind; coalescing under a fast stream; a stalled
  subscriber does not slow writes; reconnect and process restart resync; hidden page
  holds no stream; adding/removing watched sessions without reconnecting; status for
  unwatched open sessions without content notices; ~50 concurrently streaming
  sessions cost no work for unwatched content; a final settlement committed between
  watch registration and the client's first read is still displayed; a failed
  output/settlement write after subscription, with no later commit, still surfaces
  the fenced/uncertain state. Measure commit-to-render delay and per-update request
  count against current polling on the same fixture.
- Placement: no user entry before attempt; ordered promotion of several steering
  inputs; failure before/after hook; eager client; Stop races; exact receipt retry;
  ambiguous marker save → no provider call, uncertainty preserved; context excludes
  never-attempted input; not-sent discovery after a later run and reload.
- Migration preserves legacy order and invents no attempt provenance.
- Lifted caps: a tool call with >16 KiB arguments, a >16 KiB tool result and a
  >16 KiB user submission are accepted, saved, reconstructed into model context and
  served by bounded reads; existing size tests are updated, not deleted silently.

Provisional targets are recorded in the design document (decision 18). Stage 1 must
record a **baseline** for them against the synthetic 25× reference workload
(decision 26: ~100,000 entries, ~250 MB, generated in an owned temp database, never
live data): idle work, commit-to-render streaming delay, warm/cold
tab switch, main-thread long tasks, request count per streamed second, and browser
and backend memory before/after tab cycles. Cache budgets and absolute memory
numbers are set from that baseline, not fixed here.
Stage 1 proves read, notice and placement contracts — not rich rendering,
scroll geometry or overall transcript memory.

## Decisions before implementation

1. Approve this revised stage 1 (entries, reads, push, placement) as the first
   implementation scope.
2. Recheck owners and source state, then issue bounded packets 1A → 1B → 1C.
