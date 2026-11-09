# Transcript stage-1 implementation

Implemented against the approved [stage-1 plan](TRANSCRIPT-HISTORY-PLAN.md).
Packets 1A and 1B are implemented; 1C (send-attempt placement) is not included.
User entries still enter history at admission. Packet 1A's storage/read contract is
unchanged; packet 1B replaces transcript snapshot previews and polling as described below.

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
