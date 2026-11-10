# Transcript stage-1 implementation

Implemented against the approved [stage-1 plan](TRANSCRIPT-HISTORY-PLAN.md).
Packets 1A–1C are implemented. User entries enter history at send-attempt start.
Packet sections below record historical checkpoints; the audit follow-up records
subsequent corrections. Performance observations are baselines, not target guarantees.

## Packet 1A

## Storage and reads

Schema **9** rebuilds `execution_messages` with user/text/tool kinds (the internal
`role` column stores 0/1/2), stable GUID identities, nullable creation times and
exclusive `batch_index` ranges for text runs. Calls reserve entries without execution
authority; only validated request completion authorizes tools. Tool start/end times
are stored on `execution_tools`. Provider-reported usage is accumulated on the
request's `usage_json`; missing usage stays null. There is no usage route or cost estimate.

The migration merges saved tool calls into legacy message order and rebuilds
`input_decisions` and `execution_tools` to remove the three 16 KiB write caps.
Output segments and content pages retain their 16 KiB chunk size. Legacy entry/tool
times and usage stay null; no attempt provenance is added. Frozen v8 fixture tests
exercise upgrade, rollback, mixed/tool-only request order and subsequent ordinals.

`SessionBase.ReadHistoryAsync` and `SessionClient.getHistory` expose
`GET /api/sessions/{sessionId}/history`:

- Required: `mode=latest|before|after|around`, `expectedHistoryRevision`.
- All modes except `latest` require `executionId`, `entryId`, `kind=user|text|tool`.
  Together with the revision these are an exact entry reference. Latest rejects anchors.
- At most 60 descriptors, oldest first, with `hasBefore`/`hasAfter`, and settlements
  deduplicated by execution. Descriptors contain no bodies/previews or usage.
- Text descriptors include `from` and `availableThrough` cursors; batch numbering
  stays request-global. Content reads cannot cross a text-run boundary.
- One read transaction per page. Revision conflicts return 409 before anchor lookup;
  missing/wrong-session anchors return 404, with no latest-window fallback.

Tool arguments/results remain private to storage/model context in this packet;
metadata windows never decode them. Tool-body inspection is deferred by plan §2.

## Local verification and read baseline

Release backend tests, UI static checks/unit tests, offline SDK integration and
rendered browser regressions pass. Tests cover observed text/call/text ordering,
completion-only text-then-calls, failed reservations, usage/times, large payload
persistence/context reconstruction, bounded text reads, poisoned bodies and indexed
history plans without OFFSET/COUNT scans. No live data or application was used.

The owned temporary reference fixture contains **100,000 entries / 249,975,000 body
bytes**. A focused Debug run measured `latest` at **8.039 ms** with a fresh SQLite
page cache and **0.389 ms** warm median. The OS file cache was not flushed; these
are local observations, not cold-disk guarantees or performance thresholds.
`TranscriptHistoryTests.LatestWindowLatencyIsMeasuredOnAnOwnedHundredThousandEntryWorkload` reports repeatable
measurements. Browser/streaming, tab-cycle and memory baselines remain for the later
stage-1 packets that introduce those paths; packet 1A establishes only the history
read baseline.

## Packet 1B — push notices (§3 and §6)

### Aborted-state audit

The uncommitted packet already contained the session version/subscription mechanism,
bounded event hub/queues/routes, fetch SSE transport, history-backed transcript,
slim snapshots, naming migration and acceptance tests. Initial verification found
132 passing backend tests, 145/146 passing UI tests (a registration-handshake timeout),
and formatting failures in the transport and its tests. These were not treated as
completed implementation evidence.

The matching implementation was retained, not rewritten. Completion fixed:

- Client registration races: preserve the in-flight handshake during set changes,
  retain an early content notice until status and POST acknowledgement both arrive.
- Server retained registrations: repeat both status and watched-change handshakes
  when another open tab is added, even with no later session change.
- Content coordination: serialize visible rows' content reads per session, discard
  cancelled queued work, and preserve each row's coalesced trailing pass.
- Reading-position restoration: observe individual row resizing as lazy bodies load
  after a tab remount; observing only the outer message container missed movement
  above the anchor. The existing rendered anchor assertion reproduced 21 px drift
  and passes unchanged after the fix.
- Measurements: replace the inherited preview-timestamp/detection-only claims with
  an actual HEAD polling UI versus push UI comparison, described below.

Packet 1A's history/storage contract, receipt/Stop/uncertainty semantics and entry
placement were not reworked. The unrelated `AGENTS.md` edit was left untouched.

`SessionBase` owns an in-memory version and publishes after admission/pending-input
changes, request claims, output appends, observed calls, request/tool completion,
tool start, Stop, settlement, and in-memory fencing/uncertainty. Failed output or
settlement writes notify without requiring a later successful commit. Registration
subscribes and reads status/version under the session gate. The process manager
supplies a boot identity and announces committed new session identities. None of
these observations is persisted and none changes receipt, Stop or settlement authority.

The page uses one fetch/reader SSE stream, retaining `X-Urd-Client: browser`:

- `GET /api/events` starts with `event: connected`, data `{stream, boot}`.
- `POST /api/events/{stream}` replaces `{open: [session IDs], watched: [session IDs]}`;
  watched IDs must be a subset of open IDs. A successful update returns 204 and
  does not reconnect. Registration sends status first and then the watched change
  handshake before that session's first reads. Each replacement repeats these
  handshakes for retained sessions too, so a set change cannot strand a final update.
- `event: status`: `{session, boot, version, status}`. Status is `running`, `idle`,
  `failed`, `fenced` or `needs-attention`, including unwatched open sessions.
- `event: change`: `{session, boot, version}`. No content, replay or event log.
- `event: session-created`: `{session}`. Newly discovered sessions join the open
  set and receive the same status handshake. Every connection re-reads the paged
  identity list once, without reopening deliberately closed or previously omitted
  deep-link tabs. There is no list polling.
- Five-second heartbeat comments; a 30-second client watchdog and bounded retry
  repeat the handshake after failure. Hiding the page aborts its stream and reads;
  visibility/focus restarts it. Identical boot/version on reconnect still refreshes.

There are at most 128 pages per process and 128 open/watched sessions per page.
Each page coalesces one pending event per session/type and bounds creation hints at
128; overflow closes the stream so list/handshake resynchronization recovers it.
Session callbacks only enqueue, never await HTTP writes. Disconnect drops subscriptions.
The events route disables compression/buffering (`identity`, `DisableBuffering`,
`X-Accel-Buffering: no`) and streams use only one held host connection; ordinary
reads and watch updates use independent requests.

The conversation begins snapshot/history reads only after its watched handshake.
A notice during an in-flight read produces one trailing read, with the newest
notice winning. Content reads use the same coalescing rule and retain bounded
four-page passes/explicit Read more; visible rows share one content request slot
per session, and aborted queued work never dispatches. Snapshot/history metadata
remain separate from lazily read visible text bodies. Selection freezing, reading anchors, drafts
and receipt reconciliation remain intact. Tabs consume streamed status without
reading unwatched conversation content.

## Snapshot slimming (§2)

Snapshots now contain session/history/execution status, initiating model selection,
`firstUserPreview`, and `pendingInputs` (submission identity and a maximum
256-UTF-16-unit preview, at most eight outstanding inputs). They contain neither a
60-message preview window nor full pending text. The transcript reads packet-1A
history descriptors and existing bounded content cursors. `WorkspaceCommands`
names sessions only from the first user preview, never from Send dispatch or a
pending preview; otherwise the title remains `New chat`. A rendered regression
verifies that a session present before page load is discovered from the session list
and named from its first-user preview. Packet 1A entry placement is not reworked.

## Packet 1B local acceptance (§7)

Final local verification (Release, owned temporary databases, no live data):

- Backend: **133/133** tests; includes tool/pending/Stop notices, initial and later
  status, fencing and settlement registration races, final settlement before first
  read, failed output/settlement writes with no later commit, coalescing, stalled
  readers, 50 simultaneously live sessions with unwatched content suppression,
  limits, browser gate, creation, watch updates and disconnect cleanup.
- UI: format, type-aware lint and TypeScript checks pass; **148/148** unit tests.
  Transport tests exercise chunked framing, registration coalescing, early notices,
  status/ack ordering, unsubscribe, EOF/reconnect, same-version/new-connection
  notices, watchdog and abort cleanup. Content coordination tests enforce one
  active read per session and cancellation before dispatch.
- Rendered Chromium: **8/8** browser regressions, including hidden-page cleanup,
  reconnect creation/status catch-up, live discovery, initial-list discovery named
  from the first-user preview, one stream across tab changes, unwatched-tab status
  without snapshot reads, a held read followed by settlement, and selection/anchor
  preservation.
- Real-server typed-client integration: **1/1**. Vite and Release server builds pass
  with no warnings/errors. Build Vite before the server: UI assets are embedded.

The local physical-line budget is **12,528 test/helper lines < 14,328 application
lines**, counting tracked and unignored new first-party source only.

### Polling comparison

Actual rendered **`f225fa4` snapshot-polling UI/server** and working-tree push
UI/server builds were exported to owned OS-temp directories (not worktrees), built
with the same pinned tools, and measured in three alternating pairs. Every run
used a fresh owned database, session and browser context, an idle 700 ms before
Send, and the same offline loopback model fixture: a 100-character prefix followed
by a unique marker held for 1,000 ms. No live account/data was used.

A separate Node 24 worker opened only each owned `urd.sqlite` with builtin
`node:sqlite` in read-only/query-only mode. It concatenated the session's output
BLOBs and tested for the marker every approximately 2 ms. Commit lies between the
last negative query's start and first positive query's end; neither an entry's
`createdAt` nor the provider's send timestamp is a commit timestamp. A browser
MutationObserver timestamped marker insertion in the actual transcript DOM using
`performance.timeOrigin + performance.now()`. Reported commit-to-render values
are lower/upper bounds from that commit-observation interval, not exact instants.

| Pair | Old polling commit → DOM | Push commit → DOM | Push notice → DOM | UI requests old / push |
| --- | ---: | ---: | ---: | ---: |
| 1 | 200.62–202.78 ms | 23.07–25.23 ms | 24.10 ms | 4 / 3 |
| 2 | 263.72–266.00 ms | 33.95–36.33 ms | 33.60 ms | 4 / 3 |
| 3 | 267.56–269.77 ms | 34.97–37.31 ms | 34.90 ms | 4 / 3 |

Mean commit-to-DOM bounds: **243.96–246.18 ms polling**, **30.67–32.96 ms push**.
Requests are actual UI fetch starts from prefix rendered through marker rendered:
old polling made **3 snapshots + 1 content** per sample; push made **1 snapshot +
1 history + 1 content**, with one change notice. Setup, Send and the external DB
monitor are excluded. This is per held output update, not per entire execution.
Notice association is temporal by session/version; notices contain no marker text.
These small local samples are observations, not general performance guarantees.

Observed DB sampling medians were 2.05–2.12 ms (range 2.01–4.88 ms); commit brackets
were 2.15–2.37 ms wide. Median query durations were 0.042–0.099 ms, maximum 1.704 ms.
The isolated monitor yielded via `setImmediate` between sample deadlines to avoid
Windows timer granularity. Its CPU/cache contention can affect absolute timings.
Build exports and measurement databases were removed after use. Reproduction
script and detailed report remain task-local at
`%TEMP%\urd-push-measure.ZOPKLqIN\measure.mjs` and `REPORT.md`; these are ephemeral
supporting artifacts, not repository tooling or prerequisites.

The checked-in `BrowserRegression.ts` additionally renders both push and a 350 ms
polling test renderer against the **same execution**, continuously checking delivery
and request accounting. That comparator uses current bounded APIs rather than the
historical binary. Latest two samples observed push bounds 46.3–113.4 / 50.7–110.7 ms
and polling bounds 2.3–369.9 / 0.7–367.8 ms, with 3 push versus 7 polling requests.
Its looser bounds come from ordinary HTTP observations, without external DB sampling;
it is distinct from the actual-old-build comparison above.

Idle watched sessions issue no periodic reads; unwatched output causes no content
reads/notices. Notices during an active read collapse to one trailing pass. The
full 100,000-entry browser memory/tab-cycle/long-task baseline belongs to completing
stage 1 and remains unmeasured in packet 1B; packet 1A's history-read baseline above
remains the scale evidence. No packet-1C placement/attempt work was introduced.

## Packet 1B browser regression follow-up — lazy transcript anchor handoff

The tab-return anchor scenario failed **7/20** runs at packet-1B HEAD (`0536afa`),
while the matching pre-1B polling scenario passed **20/20** at `f225fa4`. Packet 1B
replaced snapshot previews with lazy history bodies. On remount, anchor restoration
scrolls programmatically; its ensuing `scroll` event was mistaken for user input and
re-anchored to a preceding user row as its lazy body appeared. That replaced the
saved assistant-row anchor and left the measured text **21 px** lower. The existing
resize observer then followed the replacement anchor instead of preserving the
reading position.

`Transcript` now records the expected scroll position for anchor corrections and
ignores the matching programmatic scroll event, retaining the saved anchor while
lazy rows resize. The browser scenario also waits for its controlled scroll event
before switching tabs, ensuring the intended starting position has been recorded;
the original 2 px assertion remains unchanged.

After the fix, the affected scenario passed **20/20** runs and the full **7-test
browser suite passed 10/10 runs**. UI checks passed, including **148/148 unit tests**;
Vite and Release server builds completed with zero warnings/errors. The older
revision's 0/20 is a local comparison, not a guarantee for other schedules or
transcript shapes.

## Packet 1C — send-attempt placement (2026-11-09)

Packet 1C is implemented; this supersedes the opening status excluding 1C. Packets
1A/1B were retained, including their history reads, notice transport and transcript
anchor handling. The unrelated local `AGENTS.md` change remains untouched.

### Plan §4 — execution and placement

Admission now retains the receipt and input without inserting a transcript entry.
A claimed input remains pending through client/options preparation. The awaited
`SessionBase.StartRequestAsync` acquires the session gate immediately before
`IChatClient.GetStreamingResponseAsync`, including eager client implementations.
It refuses after Stop or settlement and fences revisions/session ownership.
The storage transaction marks the request attempted and inserts every assigned user
entry in admission order, linked to that request. Repeating the hook does not allocate
new entries. Placement publishes the packet-1B change notice at the state-change site.

Marker-save exceptions escape provider-error classification and leave the execution
unsettled/fenced with a needs-attention notice; the provider is not called. A committed
attempt is not delivery evidence: a crash between commit and the provider call stays
uncertain. Failure after the hook retains placed input and any committed partial
output, without requeueing or automatic resubmission. Execution context excludes
prior requests known never attempted, but includes the current claim for preparation;
legacy unknown and attempted failures remain eligible with no outcome filter.

Snapshots contain only pending identity and a scalar-safe preview of at most 256
UTF-16 units, including assigned-but-not-attempted inputs. Full source remains on the
exact retained receipt route. Terminal inputs known never attempted enter a sparse
projection in the same transaction that makes them terminal (Stop, request completion
or settlement). `GET /api/sessions/{sessionId}/not-sent?cursor=...` reads at most 60
identity/execution/preview records, newest first, with a positive Int64 decimal
`nextCursor` or null. Its partial index skips sent history; no OFFSET/count scan or
unbounded body read is used. Receipt retry never starts a replacement execution.

The client separates pending previews and offers user-invoked, paged not-sent reads
and receipt retrieval. Explicit resend journals a new submission ID through the
existing uncertain-send reconciliation path; it never replaces the draft and does
not resubmit automatically. An accepted source can be explicitly resent again;
there is no new persistent resend-retention policy. Presentation is deliberately
minimal; no richer transcript layout or animation decision was made.

### Plan §5 — schema v10

The v10 migration adds `execution_requests.attempt` (0 legacy unknown, 1 known not
attempted, 2 attempted), and an `execution_inputs.not_sent` projection flag plus its
sparse `(session_id, ordinal)` partial index. Claims explicitly insert attempt 1;
existing rows retain 0 and are not guessed to be not sent.

Following the v9 rebuild precedent, `execution_messages` is rebuilt to permit a
request link on user rows while retaining legacy null links. All existing ordinals,
identities, times, ranges, unique keys, foreign keys and both explicit message indexes
are preserved. The AUTOINCREMENT high-water is retained even after deleted rows.
Foreign keys are disabled outside the migration transaction, checked before commit,
and restored afterwards. Genuine frozen v8/v9 fixtures cover upgrade and rollback;
legacy order, times, usage and attempt provenance are not invented. No live database
was opened or migrated.

### Plan §§6–7 — verification

- Release backend suite: **145/145 passed**; zero build warnings/errors.
- UI formatting, lint and TypeScript checks passed; **153/153** unit tests passed.
- Vite and Release server builds passed; offline SDK integration **1/1 passed**.
- Full browser suite **10/10 passed**, including pending promotion while reading
  (same execution, exact submission identity, unchanged 2 px anchor tolerance),
  not-sent retrieval/resend with draft preservation, and existing 1B stream/anchor
  regressions. The promotion scenario also passed five additional isolated runs.
- New backend coverage: no user entry during preparation; eager provider visibility;
  ordered multi-input promotion; idempotency and wrong-session/revision/Stop rejection;
  placement rollback; before/after-hook failures; deferred-constraint marker commit
  failure with no provider call and preserved uncertainty/status notice; exact receipt
  retry; context exclusion of never-attempted input and retention of attempted failed
  partial output; sparse pagination after later runs/reopen; bounded previews and full
  receipt source; HTTP invalid cursors and missing sessions; frozen-schema rollback.
- Independent read-only acceptance review found no supported correctness gaps.
  `git diff --check` passed.

Qualification reran the owned 100,000-entry / 249,975,000-body-byte backend fixture:
latest-history first SQLite-page-cache read **10.168 ms**, warm median (nine reads)
**0.431 ms**. The OS file cache was not flushed. A separate small browser benchmark
observed push commit-to-render bounds **37.8–99.0 / 48.6–94.0 ms**, three GETs per held
marker versus seven for the test-side polling renderer; these are not per-second or
25× measurements. Browser promotion had an earlier timing/setup failure (the test
incorrectly expected a new execution for steering, waited only about as long as
its delayed reply, and initially required observing the short-lived second request
before settlement). It now accepts the same execution's settlement and checks the
exact submission entry. The final full suite and five isolated promotion runs passed.
One intermediate full run also timed out in the existing 1B push/polling benchmark's
prefix-render wait; its isolated rerun and final full run passed without changing
that scenario. The intermittent timeout's cause was not established.

Receipt reconciliation follow-up: not-sent resend presentation now accepts only the exact
resubmission ID's accepted receipt; unrelated pending/execution activity cannot upgrade
an uncertain resend. Browser coverage verifies that a refused resend remains refused
after an unrelated successful Send, its Resend action remains available, and an exact
accepted receipt renders accepted. The unrelated benchmark prefix-render flake remains
a known non-blocking flake of unresolved cause: 15/15 isolated passes at HEAD; budgets
are unchanged.

## Stage-1 25× performance baseline (§7)

The remaining stage-1 performance acceptance item was measured on **2026-11-09**
with the built stage-1 UI and Release backend. These are local observations, not
product guarantees. The repeatable fixture generator and measurement script is
[`source/ui/tests/TranscriptBaseline.ts`](../source/ui/tests/TranscriptBaseline.ts);
run from `source/ui` with `node tests/TranscriptBaseline.ts` after building the Vite
UI and Release server. It is a standalone dev script, not included in the regular UI
or browser test suites. It initializes schema 10 through the real server, creates
six sessions, stops the server, seeds only a fresh database under the OS temp
folder, checks its foreign keys, then restarts the server. Owned processes and the
temporary fixture are removed after successful shutdown. No live application or
`C:/dev/data/urd` data is used.

Each of the five heavy tabs has 20,000 synthetic runs: 100,000 entries (20,000 user,
20,000 text and 60,000 tool entries), 60,000 tool calls, and **249,993,750 bytes**
of user-plus-assistant transcript bodies. Each includes a 5,000,000-byte reply
stored as one text entry split into 306 legal output segments, and a 5,000,000-byte
tool result. The complete stored text and tool payload is 375,111,750 bytes per
heavy session (including ordinary tool arguments/results). There is one additional,
initially empty session for streaming. Each heavy session also has 1,000 external
image URL references in a temporary sidecar manifest (5,000 total); these are not
image bodies, are not stored in the database or fetched by the browser, and are not
rendered by stage 1.

| Metric | Baseline observation |
| --- | --- |
| Idle work | Over 16.02 s: **0** same-origin page requests, instrumented JS timer callbacks or session events. Three SSE heartbeats; the two intervals were **5.014 s** and **5.014 s**. |
| Push commit-to-render | Three samples; median commit bracket to DOM-plus-next-rAF was **39.7–41.9 ms** (all observed bounds: **35.0–48.2 ms**). This is not a pixel/compositor timestamp. |
| Tab switch | First mount: median **123.4 ms** (5 tabs, range 111.7–147.7 ms). Return/remount: median **114.5 ms** (15 switches, range 112.3–131.7 ms). |
| Main-thread long tasks | **0** tasks ≥50 ms during the 10.96 s streamed reply and **0** during 4.44 s of scrolling. The observer reported long-task support. |
| Requests per streamed second | **307** same-origin requests / 10.96 s = **28.0/s**: 103 snapshot, 103 history and 101 content GETs. The run streamed 10,000 bytes of plain text in 100 × 100-byte chunks at 100 ms intervals, then settled; its already-open SSE connection and offline model request are not counted. |
| Process-tree working set before → after tab cycles | Browser: **234.9 → 297.0 MiB**; backend: **84.2 → 95.9 MiB**. Samples bracketed three cycles through all five heavy tabs. |
| `latest` history read at 100k entries | Loopback typed HTTP read (60 descriptors): first read per heavy session median **1.57 ms** (range 1.24–4.25 ms); 45 subsequent reads median **1.13 ms** (range 0.83–3.24 ms). Direct SQLite query: first sample **4.00 ms**, then warm median **0.259 ms** (9 samples). |

### Method, machine and limits

The browser run used one headless Chromium page at 1280×800, the real local Release
server, and the existing offline loopback model fixture. A read-only Node SQLite
worker sampled the stream session's output about every 2 ms; the last negative
query start and first positive query end bracket commit. A DOM `MutationObserver`
followed by `requestAnimationFrame` timestamped rendering. Tab timings run from the
actual click event through the target session's latest text appearing in the DOM
and a frame opportunity. Request counts are Playwright same-origin request starts.
The long-task observer reports browser tasks of at least 50 ms. Idle timer counts
come from instrumented page `setTimeout`/`setInterval` callbacks, not total CPU
activity. Memory is the sum of Windows process-tree `WorkingSet64` values (browser
four processes, backend two); shared pages can be counted more than once, and no
forced GC was performed. The direct query and `EXPLAIN QUERY PLAN` diagnostic uses
Node's read-only SQLite connection; it is not backend query instrumentation.

Machine context: Windows 11 Business build 10.0.26300; AMD Ryzen 9 6900HX, 16
logical CPUs, 16 GiB RAM (about 1.73 GiB free at measurement start); Node 24.16.0,
.NET SDK 10.0.403, and headless Chromium 158.0.8127.18. Vite production assets were
built before the Release server. The OS file cache was not flushed, so neither the
SQLite first samples nor the tab's first mount claim cold-disk behavior.

At this scale the latest-window plan searches `execution_messages` through
`execution_messages_history_order` with the session, revision and ordinal bounds;
execution/request/tool metadata are keyed index lookups. `EXPLAIN QUERY PLAN` shows
`SCAN m` only over the bounded 60-row window CTE, and the two `hasBefore`/`hasAfter`
lookaheads use covering searches on the history-order index. There is no base-table
scan, `OFFSET` or `COUNT` path, and the history query selects no body columns.

Stage-1 tab semantics remain simple: `Conversation` remounts on each switch and only
the latest 60 metadata entries with bounded visible bodies are loaded. Thus “cold”
and “warm” here mean first mount versus a return remount after prior reads, not the
stage-2 virtualized/retained-content paths; those timings are not comparable to the
provisional warm/cold switch targets. Scrolling exercised only the bounded latest
window, not 100,000 mounted rows. The 5 MB reply and tool result were seeded outside
the latest window and their full display/inspection paths are not present in stage
1, so their rendering cost was not measured. Likewise, stage 4 image decoding,
fetching and rendering are unmeasured; the manifest contains references only.

Two observations merit owner review when setting decision-18 targets: the live SSE
heartbeat is about 5 s (more frequent than the provisional ~30 s idle cadence),
although the idle page made no periodic reads; and process working sets were higher
after tab cycles by about 62 MiB in the browser and 11.7 MiB in the backend. The
latter is not evidence of a leak or failure to recover: this run did not force GC or
measure post-eviction settling. The streamed request rate is about three GETs per
output update, including settlement reads. These numbers seed decision-18 target
setting with the owner; they do not select budgets or promises. Full-history scroll
geometry, rich rendering, image costs, cold-disk latency, total idle CPU and memory
return-to-baseline after eviction remain unmeasured at stage 1.

## Audit follow-up — 2026-11-10

- Preserve the server's 128-session subscription bound without letting excess tabs
  break the whole page's stream. All tabs/drafts remain; the active tab always has
  live updates, excess tabs explicitly show `Updates paused`, and a warning explains
  the limit. Selecting a paused tab resynchronizes it; closing tabs restores coverage.
  This overflow policy was selected by the owner, not a larger subscription limit.
- First-user naming now reads the first visible user entry regardless of whether
  a legacy input was claimed by a request. Pending new input still has no history
  entry and cannot name a tab. A frozen-v8 migration regression covers this distinction.
- Idle heartbeats now use the agreed 30-second cadence; the client watchdog allows
  90 seconds before reconnecting. The earlier 5-second baseline remains historical.
- Local verification: 145 backend tests, 154 UI unit tests, offline integration,
  and 12 rendered browser regressions passed, including overflow discovery, selection,
  live delivery, recovery below the limit and draft preservation on one stream.
  Static checks and Vite/Release builds passed. No live data was migrated or host activated.

Tab-switch latency and streamed read amplification remain performance work, not
claimed improvements from these fixes; the 25× baseline was not rerun.

## Current-tree Stage 1 qualification and follow-up — 2026-11-10

The current working tree completed the owned 25× Stage 1 fixture after the
scroll-follow fix. This qualifies the bounded Stage 1 workload; it does not sign
off overall transcript performance, memory recovery, end-to-end pixel latency, or
the intermittent tab-return anchor assertion. The prior 25× run in this report is
a historical checkpoint; the figures below are the completed current-tree run.

The fixture has five heavy sessions, each with 100,000 entries, 60,000 tool calls,
249,993,750 transcript-content bytes and 125,118,000 tool-payload bytes, plus a
streaming session. First mount means Stage 1 remount from history; return/warm means
another remount, **not** a retained-content warm cache. These figures do not satisfy
or replace Stage 2 provisional warm/cold targets, which depend on retained content
and reusable derived structures.

| Measurement | Current-tree observation |
| --- | --- |
| First-mount tab switch | Median **112.4 ms**, range **106.1–144.9 ms** (5 tabs) |
| Return/remount tab switch | Median **130.3 ms**, range **108.3–160.3 ms** (15 switches) |
| Commit to DOM plus next rAF | Three uncertainty-expanded brackets: **39.6–44.9**, **36.8–42.3**, **39.4–44.8 ms**; median bracket **39.4–44.8 ms**. Not pixel/compositor latency. |
| Streaming GET rate | **22.74/s active** (225 GETs / 9.8945 s); **23.52/s** submit-to-final-DOM observation (238 GETs / 10.1208 s). Active snapshot/history/content: 75 each; total: 80/80/78. No approved numeric request budget. |
| Long tasks | Zero ≥50 ms during 10.12 s streaming, 4.22 s bounded-window scrolling, and heavy-tab idle. Not a frame/input-latency guarantee. |
| Idle | Heavy tab: 16.02 s, zero requests, instrumented timer callbacks or events. Empty tab: 62.29 s, zero such work and two ~30 s heartbeat observations. |
| Latest-60 history read at 100k entries | Typed HTTP: first-per-session median **7.24 ms** (n=5), subsequent **2.37 ms** (n=45). SQLite diagnostic: first **9.75 ms**, subsequent median **0.385 ms** (n=9). OS file cache not flushed. |

Memory is process-tree Windows WorkingSet64, sampled normally without forced GC;
heavy tabs remained open but unmounted while settling began on the initially empty
tab. Browser/backend MiB before cycles were **220.03/84.29**, after cycles
**283.39/95.70**, then **283.81/93.85** at 0 s, **255.04/90.45** at 5 s,
**251.85/90.45** at 15 s, **252.04/91.50** at 30 s, and **237.27/88.29** at
60 s. At 60 s this is still **+17.24/+4 MiB** over baseline. This shows settling
and partial recovery only; it does not establish memory recovery, absence of a leak,
history-length independence, or a memory budget.

### Scroll-follow and browser regression follow-up

Lazy transcript bodies can resize rows above the reading position. During tab
remount, programmatic anchor restoration emits a scroll event; treating that event
as user scrolling replaced the saved assistant-row anchor with a preceding user
row. The subsequent lazy-row resize therefore followed the wrong anchor and caused
the observed 21 px drift. The fix tracks the expected position of anchor-correction
scrolls and ignores the matching programmatic event, preserving the saved anchor
while rows resize. The corresponding browser scenario also waits for the controlled
scroll event before switching tabs.

Browser validation on the current tree: full suite **13/13**; original prefix-render
test **15/15**; delayed-follow test **10/10**; uncertain-send locator test **10/10**.
A later tab-return anchor assertion was observed once, but was not root-caused. It
had **0/25** focused reproductions; two full-suite runs passed **13/13** each. New
diagnostic assertion messages were added to make any recurrence more informative.
These results do not sign off that intermittent assertion or establish a general
reliability guarantee.

The completed run had no timeout, but the nominal tab-switch figures remain unmet
and the 60-second memory sample remains above baseline. End-to-end pixel latency,
overall performance, memory recovery, and Stage 2 warm/cold target performance are
not signed off. Stage 2 rich rendering, retained warm-content/cache paths,
full-history virtualization, cold-disk behavior, and longer memory/eviction studies
remain outside this Stage 1 qualification.

## Pre-release schema reset — 2026-11-10

The current schema is **11**. It is created atomically on a fresh/version-0 database;
version 11 reopens, and any other version fails with manual reset instructions. Stop
Urd, delete `urd.sqlite`, `urd.sqlite-wal` and `urd.sqlite-shm`, then restart.
No database files are deleted automatically. Historical upgrade/rebuild/backfill
code and fixtures are removed. Earlier packet and performance observations above
remain dated evidence, not a compatibility promise.

Requests now have only known not-attempted (1) or attempted (2) provenance. User
transcript rows require request links; receipts require their original selection.
Storage names are `response_id`/`response_final` and request-global chunk indexes.
The standalone baseline fixture follows the current schema. Browser workspace
version 2 stores explicit title provenance and discards incompatible saved state
rather than migrating it. The pre-release policy is recorded in `AGENTS.md`.

Local reset verification passed: Release backend **141/141**, UI static checks and
**162/162** unit tests, rebuilt Vite/Release assets, offline integration **1/1** and
rendered browser regressions **14/14**, including provider-settings focus/navigation
and not-sent resend outcomes. The standalone current-schema baseline completed all
five 100,000-entry fixtures and 21 tab switches with no page errors; its temporary
data was removed. This verifies fixture compatibility, not new performance budgets.
Independent review's unversioned-nonempty database finding was fixed and covered;
follow-up review found no supported issues.
