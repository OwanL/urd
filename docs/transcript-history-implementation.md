# Transcript packet 1A

Implemented against the approved [stage-1 plan](TRANSCRIPT-HISTORY-PLAN.md).
Packets 1B (push/snapshot slimming) and 1C (send-attempt placement) are not included.
User entries still enter history at admission; snapshots still contain text previews.

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
