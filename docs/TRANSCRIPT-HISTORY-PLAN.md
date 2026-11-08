# Transcript stage 1 — history and navigation foundation

**Status: concrete proposal for review, not implementation approval.**
Source checked at `5c9392f` on 2026-11-09. Requirements are owned by
[Transcript design](TRANSCRIPT-DESIGN.md); parsing evidence is separate.
No production code, dependency, migration or running-application change is made here.

## Outcome and explicit boundary

Make saved conversation history addressable without loading bodies or walking from
the newest page. Place newly accepted input in the conversation when a send attempt
starts, not merely when the submission is accepted. Preserve receipt idempotency,
execution authority, Stop, failure and uncertainty guarantees.

This stage is **not the finished whole-history scrollbar**. Message rank/count is
useful for indexed navigation, but is not an estimate of rendered height. One huge
reply must eventually occupy more scroll space than a short prompt. Weighted
structural estimates and within-message positions belong with stage 2's formatting
projection. Do not ship an equal-height/count-based scrollbar as satisfying that
requirement, or persist cumulative pixel offsets that require rewriting every later
row when earlier content grows/reflows.

No Markdown parser, DOM virtualizer, tool/reasoning projection, attachment delivery,
search, transport replacement or custom event framework is included. Keep current
text content reads and their UTF-8 cursors. Minimal pending-input UI integration is
required if admission-time conversation rows are removed; do not make accepted text
silently disappear while waiting for stage 2.

## 1. Identity and indexed ordering

- Keep `MessageRef` (execution, role, message ID, history revision) as the identity of
  an existing text message. Requests, executions, messages and submission IDs remain
  different concepts. A future content-part anchor can extend a message location;
  do not pretend tool calls already are these text messages.
- Keep global `execution_messages.ordinal` as insertion evidence. It is not a
  session rank: other sessions create gaps.
- Proposed addition: dense zero-based `position` per session/history revision,
  allocated only when a visible message is inserted. Add an index on
  `(session_id, history_revision, position)` and a small per-history summary with
  message count and an observation version. Allocation, row insertion and summary
  update occur in the same transaction; replay must not increment them twice.
- Existing `(run_id, role, message_id)` identity uniqueness remains. New input rows
  are ordered by their admitted input order **within the request being started**.
  Assistant rows still enter on first saved output segment, not on request claim.
- Backfill positions once in existing ordinal order. Do not rescan/recount history
  during each window request. Revisions scope positions; they are not permanent
  identities across suffix replacement or future content-index reconstruction.
- Return new Int64 positions/counts/versions as invariant decimal strings, not JS
  numbers. Existing revision/segment numeric contracts are unchanged in this stage;
  their pre-existing safe-integer limitation is not solved by this addition.

This deliberately adds rank navigation, not a speculative weighted tree. Stage 2
must choose and qualify indexed weighted lookup with its actual structural units;
rank lookup alone does not close that gate. No parser or body scan is needed for
stage 1's steady-state index maintenance.

## 2. Bounded history reads

Proposed route: `GET /api/sessions/{sessionId}/history`. One reader/contract supports
mutually exclusive modes rather than separate implementations of nearly identical
queries. Concrete public names below are proposals to settle before coding.

| Mode | Input | Result |
| --- | --- | --- |
| `latest` | Expected history revision | Newest bounded window, oldest first |
| `before` | Exact message reference + expected revision | Strictly earlier neighbors, oldest first |
| `after` | Exact message reference + expected revision | Strictly later neighbors, oldest first |
| `around` | Exact message reference + expected revision | Window including that exact anchor |
| `position` | Decimal rank + expected revision | Window around a known message rank, not a pixel fraction |

Reuse the existing 60-message ceiling as an initial **metadata page size**, not a
new source-memory/cache budget. Allow smaller positive limits up to that ceiling;
reject invalid limits/mixed selectors. For `around`/`position`, aim for half the
remaining slots before the anchor, then after; fill unused slots from the other
side near a boundary. Empty history returns an empty latest page. A rank outside
nonempty history or missing exact anchor is not silently changed to latest.

One transaction reads revision, summary, bounded message identities, lifecycle
metadata and boundary flags. Descriptors contain no text preview/body:
- message reference and position;
- committed content extent (`AvailableThrough` using the existing cursor meaning);
- message closure, final-output marker and execution settlement as distinct facts.

Envelope: session/revision, observation version, message count, chronological
entries, oldest/newest references, and `hasBefore`/`hasAfter`. Boundary flags use
indexed existence/lookahead, not full counts. Exact message identity must be checked
against the addressed session before an index seek; a reference from another session
is not a position shortcut.

Expected revision is checked **before anchor lookup**, including an empty page:
stale revision follows existing HTTP 409 behavior; absent anchor/session follows
existing not-found conventions; malformed selector follows validation conventions.
Do not invent a latest-history fallback in the server. Stage 2 owns the user-visible
removed-anchor recovery policy.

### Consistency, not a frozen paging snapshot

Each page is one coherent observation. Appends can occur between pages without a
history revision change. `before`/`after` keysets preserve existing neighbors while
new tail entries arrive; callers merge by identity and compare observations, not
concatenate as if every response belonged to one frozen history.

Increment the observation version for changes affecting returned descriptors or
summary: message insertion, committed content advancement/final marker, and relevant
closure/settlement. Do not bump it for unrelated provider settings or navigation.
It is a change token, not a durable event stream. Audit all mutation paths, including
terminal updates, when implementing; a count-only version would miss live-tail data.

Snapshot and history are separate observations unless explicitly read together.
Do not claim that separately requested HTTP responses share a transaction. Keep
existing bounded snapshot previews for compatibility, but implement their message
selection/lifecycle joins through shared helpers where useful to avoid drifting
semantics; the new metadata endpoint itself must never call `ReadPreview`.

## 3. Conversation promotion at a send attempt

### Chosen proposal: application send-attempt boundary

After the borrowed chat client and request options are prepared, immediately before
calling `IChatClient.GetStreamingResponseAsync`, call an awaited session-owned
operation, proposed name `StartRequestAsync`. It transactionally marks the request
attempt and promotes only that request's assigned inputs. Calling the marker only
before `MoveNextAsync` is too late for injected clients that start work eagerly.

This is an **attempt**, not proof that the provider/model received anything.
Provider wrappers currently do some authentication/validation lazily during
enumeration; those later failures therefore stay with the attempted conversation
message. Keeping that simple boundary avoids threading a delivery callback through
every provider adapter. It is the proposed implementation interpretation of the
owner's failed-send example, not a claim that claim/receipt timing proves delivery.

The database commit and external invocation cannot be atomic. A crash/Stop between
them may leave an attempted message that was never transmitted. Preserve that
uncertainty; do not requeue or silently erase it. No exact network-delivery protocol
is proposed.

### Required state and transitions

Add one request-level attempt fact, not per-input independent dispatch state:
new requests start `not attempted`; crossing the hook sets `attempted`; legacy
requests can be `unknown`. Their assigned inputs inherit that fact.

| Event | Durable/input behavior | Conversation behavior |
| --- | --- | --- |
| Accept | Save receipt and execution input as today | Pending, no new normal message row |
| Claim | Bind input batch to a request and freeze context as today | Still pending |
| Start attempt | Validate owner/revision/non-stopping run and matching unfinished request; commit marker + ordered input rows atomically | Promote once, before calling client |
| Later failure/Stop | Preserve claim and attempt; use existing cleanup/settlement | Keep message with failed/cancelled/uncertain attempt |
| Failure/Stop before attempt | Retire according to existing execution rules; never auto-resend | Keep recoverable not-sent input separate, not a normal conversation message |
| Exact submission retry | Return original receipt; no new admission or send | No duplicate message/promotion |

`StartRequestAsync` must join the existing session gate/ownership discipline and be
idempotent for the same request. Stop winning the gate prevents a fresh attempt;
a marker already committed remains evidence of an attempt even if cancellation wins
before invocation. Do not hold a database/session lock during provider execution.

A rejected attempt operation must not call the provider. An exception/ambiguous
save must escape as execution uncertainty using the established fencing path, not
be caught as an ordinary provider error and falsely settled. Cover the broad catch
in `SendRequestAsync` explicitly; the output-save uncertainty pattern is relevant.

### Context eligibility is part of this change

`LlmContextReader` currently includes historical user inputs when merely claimed.
Keep the **current** request's assigned inputs while constructing its frozen context,
but exclude prior requests known never to have crossed the attempt boundary. Keep
legacy-unknown inputs compatible with existing history rather than guessing they
were unsent. Preserve request/tool/provider-context ordering and continuation rules.
Without this change, a pre-attempt failure could invisibly reappear in a later model
request despite never becoming a conversation message.

## 4. Pending and never-sent inputs

Expose pending input independently of `Messages`, using existing execution/input
identities. Add a bounded snapshot projection for the current execution's waiting
inputs; reuse the existing outstanding-input cap rather than hydrate every accepted
submission in history. Distinguish waiting from claimed-but-not-attempted internally;
neither means delivered. Input body retrieval stays separate from metadata.

Proposed recovery rule: retain terminal never-attempted input as **not sent**, with
copy/explicit resend available; do not overwrite the current composer draft. An
explicit resend uses a new submission ID. Reusing the original ID must continue to
return the original receipt. Recovery needs **discovery**, not only a body-read API:
after later executions and reload, the client may no longer know the older execution
or submission ID.

Proposed bounded discovery: a session-scoped `not-sent` listing, keyset-paged in
input order, backed by a sparse derived index of terminal, known-unattempted input
references. Update it transactionally when those inputs retire; do not scan all
successful inputs to find rare failures. A snapshot may expose an indexed existence
flag, not the whole archive. Records identify execution/submission, outcome and
ordering cursor; retrieve original text using the **existing receipt read**
(`GET /message-submissions/{submissionId}`), which already returns the retained
submission. Preserve its current text bound. Do not duplicate the submission body.
The sparse projection is not a second dispatch authority; attempted/legacy-unknown
requests must never be inferred to be definitely not sent.

Provide an explicit on-demand way to open that listing after reload, independent of
the latest settlement or local submission journal. No unsolicited popup, automatic
retry, queue editing, dismissal protocol or general execution archive is proposed.

The exact pending/not-sent presentation and minimal client wiring are part of the
1B plan review, before removing admission-time rows. Do not implement invisible
pending-state changes backend-only and declare the stage complete.

## 5. Existing data

Schema v8 has no send-attempt fact. Existing output supports an attempt; absence of
output does not prove no attempt. Original admission-time ordinals cannot reconstruct
true send chronology. Recommended migration: **preserve existing visible rows/order**,
backfill dense positions in that order, and leave missing attempt provenance unknown.
New writes follow the new placement rule. Do not guess, delete accepted text, replay
work or reorder old conversations to fabricate historical certainty.

A genuine old-schema fixture must test upgrade and rollback, including existing
failed/unsettled requests. No live `C:/dev/data/urd` migration is authorized by this
plan. Future revision replacement must rebuild/remove matching derived positions
and reject stale anchors; this stage does not add a history-edit command.

## 6. Implementation packets and ownership

These packets are sequential where they share contracts; they are not parallel
assignments to edit the same storage/session files.

**1A — metadata navigation, old placement preserved.** Finalize contracts; migrate
rank/count/version metadata; add indexed history reader/core method/server route and
typed client calls. Cover reads, migration and version updates. No UI scrollbar or
pending-input behavior changes yet. This packet can be reviewed independently.

**1B — send-attempt placement, complete small vertical slice.** Add the attempt
fact/hook, context eligibility, pending/not-sent projection and minimal client
presentation together. Coordinate Workspace/Transcript ownership, reuse the newly
extracted Workspace command/persistence boundaries, and retain rendered regression
coverage. Do not mix this with rich Markdown or CSS redesign.

Likely owning files:
- Backend: `core/sessions/Messages.cs`, `SessionSnapshot.cs`, `SessionBase.cs`;
  `core/storage/DatabaseSchema.cs`, `SnapshotReader.cs`, `MessageContentReader.cs`,
  `UserMessageStorage.cs`, `SessionExecutionStorage.cs`, `LlmResponseStorage.cs`,
  `LlmContextReader.cs`; `core/execution/SessionExecution.cs`.
- New focused history reader/contract files as appropriate; no giant generic service.
- Hosting: `source/hosting/server/SessionRoutes.cs`.
- UI: `source/ui/sessions/Messages.ts`, `SessionClient.ts`; minimal pending projection
  integration in the existing workspace/transcript owners after coordination.

## 7. Acceptance tests and stop conditions

Reuse `SessionSnapshotStorageTests`' synthetic seeding, poisoned omitted bodies and
query-plan helpers; extract helpers rather than copy entire fixtures. Use existing
`MessageContentTests`, `ExecutionStorageTests`, `SteeringFlowTests`, `SessionStopTests`,
`SessionExecutionTests`, `SessionRoutesTests`, `SessionClient.test.ts` and the offline
rendered workspace regression where their behavior is affected.

- Latest/before/after/around/rank: boundaries, empty history, exact anchor inclusion,
  wrong-session identity, stale revision before lookup, bounded pages, consistent
  flags/count/version and identity-based merge under append.
- Interleaved sessions and global ordinal gaps; direct distant-rank lookup with no
  OFFSET or repeated whole-history COUNT; poisoned bodies prove metadata reads do
  not decode text. A large omitted body must not affect response payload size.
- Compare synthetic small and long histories using query plans and observed work;
  do not assert that a SEARCH plan alone proves flat latency. Measure write overhead
  from the summary/version updates as well as read speed.
- Promotion: blocked fake client, multiple admitted steering inputs, no row before
  attempt, one ordered promotion, failure after hook, local failure before hook,
  synchronous/eager client behavior, Stop races and exact receipt retry.
- Ambiguous marker-save failure must invoke no provider and preserve uncertainty;
  do not silently mark it failed-and-retryable. Existing tool cleanup/authority
  and unowned-execution behavior must remain unchanged.
- Context excludes known unattempted prior inputs while retaining the current batch
  and legacy history. Never infer tool non-execution from missing display rows.
- Waiting/not-sent text remains recoverable without overwriting drafts; explicit
  resend creates new identity, while retrying the old identity does not resend.
  Cover pre-attempt failure, a later successful execution, browser reload, then
  discovery/copy/resend of the original not-sent input. Its discovery query must stay
  bounded when surrounded by many successful inputs.
- Real migration and reopen preserve old content/order and do not fabricate attempt
  provenance. New count/version/position strings round-trip above JS safe-integer
  range using small focused fixtures, without allocating that many history rows.

No fixed millisecond, row-count scale ceiling or cache budget is selected here.
Record representative query/allocation results before signoff; regressions or
unbounded scans are blockers, not reasons to quietly raise limits. Stage 1 completion
proves the addressed read/placement contracts—not rich rendering, estimated pixel
geometry, full-history UI navigation or overall transcript memory performance.

## Review status

Independent source-grounded review found a missing discovery path for old never-sent
inputs. Section 4 and its failure → later execution → reload regression now close
that gap; targeted re-review approved the correction. This was static plan review,
not implementation or runtime qualification. Packet 1A does not depend on building
the separate not-sent discovery feature in packet 1B.

## Review decisions before implementation

1. Approve packet 1A as the first implementation boundary, with rank explicitly
   distinct from scrollbar geometry; or require weighted positioning in this stage.
2. Confirm the proposed application-level attempt interpretation and minimal
   pending/not-sent recovery slice before packet 1B.
3. Recheck current owners and actual source state, then issue bounded implementation
   work. This plan alone does not authorize source edits, installation or activation.
