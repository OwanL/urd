# Transcript stage 1 — history, live updates and placement foundation

**Status: concrete proposal for review, not implementation approval.**
Source checked at `5c9392f` on 2026-11-09. Requirements are owned by
[Transcript design](TRANSCRIPT-DESIGN.md); parsing evidence is separate.
No production code, dependency, migration or running-application change is made here.
Revised 2026-11-09 after checking the plan against the overall requirements
(low overhead, snappy streaming, tool rows, images, Markdown, animation).

## Outcome and explicit boundary

Build the foundation later stages render on:
1. a history index whose unit is a **transcript entry** (user message, assistant
   text, tool call), addressable without loading bodies;
2. bounded anchor-based reads of that index;
3. **push change notices** replacing 350 ms snapshot polling;
4. input placed in the conversation when a send attempt starts.

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
  warrants the churn) to hold **kind-tagged entries**: `user`, `assistant`, `tool`.
  Reasoning and attachment-bearing kinds are added later without changing anchors.
- Entry identity is a stable reference: execution, kind, entry ID, history revision.
  `MessageRef` stays valid for text entries; tool entries reference
  `(request, call ID)` and carry a GUID entry ID like the others. Do not overload
  request, execution, submission and entry identities.
- Order is the index's insertion order (`ordinal`), scoped by session/revision
  through the existing `(session_id, history_revision, ordinal)` index. Global
  ordinal gaps are fine for keyset reads; they are not ranks and never displayed.
- Insertion points: user entries at send-attempt start (section 4); assistant entry
  on first saved output segment (unchanged); tool entries when the call is saved
  at request completion, ordered by `call_index`. Stage 3's draft calls must
  allocate the entry when first observed and keep it through finalization — note
  now, implement then.
- Migration backfills tool entries for existing saved calls after their request's
  assistant entry (or in request order when none exists), preserving existing
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
  Text: committed extent (`AvailableThrough`), closed, final-output marker.
  Tool: name, started/outcome. Execution settlement is reported per execution once
  per page, not repeated per entry. Arguments/results stay out (stage 3 inspection).
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

- One streamed response per **visible** session: `GET .../events`, consumed with
  `fetch` + a stream reader, so the existing `X-Urd-Client` header gate is kept
  (`EventSource` cannot send it). Server-Sent Events framing is sufficient; no
  WebSocket, no new dependency.
- Notices are tiny and coalescing: `{ boot, version }` plus optionally the changed
  entry kinds. No content, no event log, no replay. The client compares versions and
  reads what is on screen (history window tail, open content) — at most one read in
  flight per kind, newest notice wins.
- `SessionBase` owns all session writes, so it bumps an **in-memory** counter after
  each change that affects reads: committed changes (entry insert, output append,
  tool state, settlement, pending input, Stop) **and in-memory state changes the
  snapshot exposes**, such as admission fencing after an unexpected execution
  failure or a failed store write. Bump the counter where the state changes, not
  per storage call, so no observable transition is missed. `boot` identifies the
  process so restarts force a full resync. Nothing is persisted.
- Writers never wait for subscribers: a slow or vanished client only misses
  intermediate versions. Bound subscribers per session; drop on disconnect.
- On connect/reconnect the client does a normal snapshot + history read; the stream
  only says "something changed". Hidden tabs hold no stream (consistent with
  eviction); focus/visibility reconnects.
- **No lost final update — subscribe first, then read.** The server registers the
  subscriber and reads the current version under the session gate, then sends that
  as the first event. The client starts its snapshot/history reads only after that
  first event. Any commit after registration therefore produces a later notice; any
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
- Pending inputs appear in the snapshot (bounded by the existing outstanding cap).
  Terminal never-attempted inputs are **not sent**: discoverable through a sparse,
  keyset-paged session listing; text read through the existing receipt route;
  explicit resend uses a new submission ID; drafts are never overwritten.

Promotion from pending to conversation is a visible transition. It must not move
content the user is reading: append below the reading anchor, and when following
the tail, keep following. Any animation is transform/opacity only, respects reduced
motion, and does not delay the state change.

## 5. Existing data

Schema v8 has no attempt fact. Preserve existing visible rows and order, mark
attempt provenance unknown, backfill tool entries as above. No guessed reordering,
deletion or replay. Genuine old-schema fixture tests upgrade/rollback. No live
`C:/dev/data/urd` migration is authorized by this plan.

## 6. Implementation packets

Sequential; they share storage/session files.

**1A — entry index and history reads.** Migration (entry kinds, tool backfill),
history reader, core method, route, typed client. Old user placement unchanged.

**1B — push notices.** In-memory session version, events route, client stream
reader replacing `useSessionSnapshot` polling for visible sessions; snapshot slimmed
once the transcript reads history. Keep the existing rendered regressions passing.

**1C — send-attempt placement.** Attempt fact/hook, context eligibility, pending and
not-sent projection with minimal client presentation, promotion without anchor
movement.

Likely owning files: backend `core/sessions/` (`Messages.cs`, `SessionSnapshot.cs`,
`SessionBase.cs`), `core/storage/` (`DatabaseSchema.cs`, `SnapshotReader.cs`,
`MessageContentReader.cs`, `UserMessageStorage.cs`, `SessionExecutionStorage.cs`,
`LlmResponseStorage.cs`, `LlmContextReader.cs`), `core/execution/SessionExecution.cs`,
a new focused history reader; `source/hosting/server/SessionRoutes.cs`; UI
`sessions/Messages.ts`, `SessionClient.ts`, `app/useSessionSnapshot.ts` and the
existing workspace/transcript owners after coordination.

## 7. Acceptance checks

Reuse `SessionSnapshotStorageTests` seeding/poisoned-body/query-plan helpers,
`MessageContentTests`, `ExecutionStorageTests`, `SteeringFlowTests`,
`SessionStopTests`, `SessionExecutionTests`, `SessionRoutesTests`,
`SessionClient.test.ts` and the offline rendered workspace regression.

- Reads: all modes, boundaries, empty history, wrong-session anchor, stale revision,
  mixed kinds including a tool-only request, interleaved sessions; poisoned bodies
  prove no text/argument decoding; indexed plans without OFFSET/COUNT scans.
- Push: notice after each change kind; coalescing under a fast stream; a stalled
  subscriber does not slow writes; reconnect and process restart resync; hidden tab
  holds no stream; a final settlement committed between server registration and the
  client's first read is still displayed; a failed output/settlement write after
  subscription, with no later commit, still surfaces the fenced/uncertain state. Measure commit-to-render delay and per-update request count
  against current polling on the same fixture.
- Placement: no user entry before attempt; ordered promotion of several steering
  inputs; failure before/after hook; eager client; Stop races; exact receipt retry;
  ambiguous marker save → no provider call, uncertainty preserved; context excludes
  never-attempted input; not-sent discovery after a later run and reload.
- Migration preserves legacy order and invents no attempt provenance.

No millisecond targets or cache budgets are fixed here; record measured results.
Stage 1 proves read, notice and placement contracts — not rich rendering,
scroll geometry or overall transcript memory.

## Decisions before implementation

1. Approve this revised stage 1 (entries, reads, push, placement) as the first
   implementation scope.
2. Recheck owners and source state, then issue bounded packets 1A → 1B → 1C.
