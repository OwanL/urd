# Segmented binary log + explicit index adapter (family 6)

Stage adapter for the transcript-storage experiment programme: append-only,
immutable per-session binary segments plus one explicitly specified external
index — SQLite (`Microsoft.Data.Sqlite`, pinned **10.0.14**) as the AUTHORITY
for sessions, ordinals, `(segment, offset)` pointers, roles, preview metadata,
and assignments. Correctness scope only — no tuning, no benchmark, and the
shared runner owns verification and timing.

This directory owns: the adapter project, its test project, and this README.
No shared harness, runner, or solution files are edited.

## Layout

- `<storeRoot>/index.db` — SQLite coordination tables:
  - `session_ptr` — pointer row per session: count, active segment id, and the
    active segment's indexed end byte (the append position). This is the sole
    authority for "where the session's acknowledged content ends".
  - `messages` — the ordinal → (segment, offset, length) map plus role and the
    write-time preview-metadata row per part.
  - `assignment` + `assign_index` — assignment records and the
    (target-session, id, status) index for the deletion policies.
  - `draft_marker` + `draft_batch` — active stream marker and one persisted
    row per `StreamDraft` call.
- `sessions/<hex(utf8(name))>/seg-NNNNNN.log` — immutable append-only
  segments. Record framing (the whole codec, documented):
  `[int32 little-endian payload length][payload]` — payload is the same
  newline-free JSON message body the JSONL family uses; the length prefix is
  the only framing metadata (no CRC, no header record). Recovery never needs
  to parse records because the index is authoritative.

## Segment budget and rolls

8 MiB target per segment file. A record that does not fit rolls the session
to a fresh segment so budgeted segments only ever hold whole frames. A single
record whose frame exceeds the target is the OVERSIZED exception: it is the
sole occupant of a dedicated segment, and the next append starts yet another
segment.

## Suffix edits without a whole-session rewrite

`ReplaceSuffix` APPENDS the replacement records as fresh frames (old bytes
are never rewritten), then replaces the index suffix — delete pointer rows
≥ fromOrdinal, insert the new pointers, update count and pointer — in ONE
SQLite transaction. The superseded payload bytes remain in the segments as
explicit garbage; the cumulative total is reported via
`RetainedGarbageBytes` and no compaction is claimed for initial
qualification. Reads resolve only indexed `(segment, offset)` windows, so
garbage is never served and never mistaken for state.

## Recovery and cleanup limits (declared)

Recovery runs at `Open` and is index-authoritative (recovery by content):

- The ACTIVE segment's unindexed tail — an interrupted, unacknowledged data
  stage — is truncated to the authoritative append position (`cur_end`). This
  is enforced even when the index count is zero (e.g. after a suffix edit down
  to an empty session, or a fresh oversized-record slot where `cur_end` = 0); a
  file shorter than `cur_end` is corruption. More generally, EVERY
  referenced segment is truncated to its furthest indexed byte: never below
  it, and referenced bytes are never touched. Recovery is by content — the
  index is authoritative, never the raw file length.
- Wholly unreferenced segments (files with an id beyond the session's active
  pointer) are deleted; the pointer advances only with an acknowledged index
  transaction, so those bytes can never be pointed at again.
- Unreferenced session directories (deleted sessions) are swept.
- Referenced segment files are NEVER deleted and NEVER truncated below their
  furthest indexed byte. Superseded-edit garbage inside older referenced
  segments is retained and never served (explicit, counted; no automatic
  reclaim).
- A referenced segment file that is missing, or shorter than its furthest
  indexed byte, is corruption and fails loudly — records are never dropped.

## Durability ordering and ack tier: OsFlush

- Commit ordering per mutation: segment bytes are written and `Flush()`ed
  (OS buffer cache — no fsync; the .NET flush-to-disk option
  `FileStream.Flush(flushToDisk: true)` exists and is deliberately NOT used,
  so `OsFlush` is a chosen, declared tier — not forced) FIRST; the SQLite
  index transaction SECOND; acknowledged only after it commits.
- SQLite runs on all defaults (`journal_mode=DELETE`, `synchronous=FULL`) —
  the index commit fsyncs its journal, but the declared tier is OsFlush
  because the segment data stage is the weakest link. No power-loss claim.
- A process kill therefore leaves either the before-state or the after-state;
  an interrupted segment write is an unindexed tail truncated at Open. A
  missing or short referenced segment fails loudly instead of being
  "repaired" by silently dropping records.

## Streaming and deletion contracts

- `StreamDraft` commits one persisted batch per call (marker + batch row;
  nothing is buffered until finalize; batches survive reopen; restart of the
  same streamId is accepted; one active draft per session; unfinalized drafts
  never appear in any read window). `FinalizeStream` appends the message
  frame, flushes, then commits the pointer row + count + marker/batch removal
  in one transaction, and works as a brand-new session's first message.
- `DeleteSession` commits the `RequireSettled` / `CancelOpen` assignment
  invariant atomically with the catalog, pointer, and stream removal — no
  dangling open references; cancelled and settled assignment records survive.
  The session's segment directory is deleted best-effort; the next Open
  reclaims unreferenced dirs.

## Tests

`tests/Urd.TranscriptHarness.SegLog.Tests` — focused xUnit facts over
synthetic OS-temp data with a test-only smaller segment target (the public
parameterless constructor keeps the 8 MiB default, as the plugin loader
requires): oracle-verified appends/windows/reopen, multi-segment rolling,
oversized dedicated segments, segment immutability across edits with
retained-garbage accounting, streamed batches (persist, restart, conflict,
atomic finalize incl. first message), assignment/deletion policies, bounded
preview/fetch (incl. beyond-cap fallback, unicode mid-pair cuts), and
recover/corruption/sweep behaviors as declared above.