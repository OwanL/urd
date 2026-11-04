# JSONL offset-index adapter (family 10)

Stage bounded-read reference adapter for the transcript-storage experiment programme:
one JSONL file per session (one message record per line) plus a small SQLite
coordination database per store root (Microsoft.Data.Sqlite, pinned **10.0.14**,
the same version the SQLite-rows family uses). Correctness scope only — no
tuning, no measurement; the shared runner owns verification and timing, and the
hybrid index cost is documented honestly below.

This directory owns: the adapter project, its test project, and this README.
No shared harness, runner, or solution files are edited.

## Layout (declared in `Describe`)

- `<storeRoot>/index.db` — SQLite coordination tables:
  - `sessions` — authoritative per-session pointer row: count, generation,
    current file name, and indexed byte length.
  - `messages` — per-message row: byte `offset` + `length` into the JSONL file
    (the persisted offset index), role, and a preview metadata row.
  - `assignments` + `assign_index` — assignment records and the
    (target-session, id, status) index, so open-assignment checks are one
    indexed lookup (used by the deletion policies).
  - `draft_marker` + `draft_batch` — active stream marker and one persisted
    row per `StreamDraft` call.
- `sessions/<hex(utf8(name))>/messages-<gen>.jsonl` — the transcript records;
  directory and file names are addressed only through the published pointer
  row; generation is advisory numbering, the pointer is authoritative.
- Values: JSON messages (one object per line; every newline is escaped inside
  string values, so a line always equals one record). Tool results stay
  **inline** in the line.

## Bounded reads (no whole-session memory mirror)

- Every read resolves a window through the SQLite index (ordinals + offsets)
  and then fetches exactly those single lines by byte offset; nothing scans a
  file end to end and no in-process copy of a session is kept.
- The preview index stores per part: kind, toolCallId (paired kinds), full
  content character length, and a bounded preview string — computed once at
  write time, capped at 64 UTF-16 code units (the same unit the harness
  compares with `string.Length` and `content[..n]` slicing). Preview requests
  at or under the cap decode only persisted short strings; requests beyond the
  cap fall back to bounded per-row reads (only the requested window's lines are
  fetched and decoded). Trade-off recorded: the persisted cap saves all large
  content decoding for normal UI-sized requests; larger requests pay
  count-window rows once.
- `FetchToolResult` is the targeted single-result read: exactly one message
  line is fetched and decoded, exact full content returned.

## Durability ordering and ack tier: OsFlush

- Commit ordering per mutation: JSONL line(s) are written and `Flush()`ed (OS
  buffer cache — **no fsync**; the .NET flush-to-disk option
  (`FileStream.Flush(flushToDisk: true)`) exists and is deliberately not used,
  so OsFlush is a chosen, declared tier rather than a forced one) FIRST; the
  SQLite index transaction SECOND; the
  mutation is acknowledged only after the transaction commits.
- The SQLite side runs on all defaults (`journal_mode=DELETE` rollback journal,
  `synchronous=FULL`), so the transaction commit fsyncs its journal — but the
  declared tier is still **OsFlush** because the JSONL data stage is the
  weakest link and is never fsynced. No WAL/tier overrides anywhere.
- A process kill therefore leaves either the before-state or the after-state:
  an interrupted data stage produces an unindexed tail that **Open truncates**
  (recovery is by content — the indexed prefix is authoritative; a referenced
  file missing or shorter than the index is corruption and fails loudly).

## Orphan cleanup (declared, lazy)

- At `Open`: unindexed JSONL tails are truncated; session directories without
  a pointer row (deleted sessions, e.g. after a crash between the catalog
  commit and the best-effort file delete) are deleted; files inside a
  referenced session directory other than the published file (interrupted
  generation rewrites) are deleted.
- After publication: the superseded generation file is deleted best-effort
  immediately; any failure defers to the next Open pass. Nothing is served
  from an unreferenced file at any time, and orphans are never counted as
  state.

## Edit mechanics and costs

- `ReplaceSuffix` is a **generation rewrite**: the kept prefix `[0, k)` is
  copied byte-identically (kept lines are not re-encoded), replacements are
  appended, and only then is the pointer (generation/file/bytes/count), the
  new offset rows, and the removed old rows published in **one SQLite
  transaction**. Cost declared honestly: O(session bytes) per edit — the
  whole prefix is rewritten; there is no in-place edit and **no cross-file
  atomicity**: a crash mid-rewrite (or between publication and old-file
  delete) leaves only unreferenced orphan files, cleaned at the next Open.
- Streaming: every `StreamDraft` call commits its own SQLite transaction (one
  persisted batch row; nothing is buffered until finalize). `FinalizeStream`
  appends the message line, then commits message row + count + marker and
  batch deletion in one transaction, so a finalized stream is either fully
  inserted or absent, and an unfinalized draft's batches are never served as
  messages.
- Deletion: `DeleteSession` enforces the frozen invariants — `RequireSettled`
  fails while open assignments target the session; `CancelOpen` cancels them
  in the same transaction as the catalog/assignment-index/stream removal.
  Cancelled and settled assignment records survive; dangling open references
  are never created.

## Tests

`tests/Urd.TranscriptHarness.Jsonl.Tests` — focused xUnit facts over synthetic
OS-temp data: oracle-verified appends/windows, persisted streaming batches
(incl. stream restart across reopen and single-draft conflicts), suffix edit
renumbering/truncation with reopen (no stale content), assignment and deletion
policies, bounded preview/fetch (incl. beyond-cap equivalence and unicode
boundary slicing), orphan-truncation/sweep recovery, and descriptor honesty.
Verification uses the shared oracle (`Oracle.VerifyAll` with `ModelOps`)
against the real store process.