# RocksDB indexed-records adapter (family 5)

Stage 0 adapter for the transcript-storage experiment programme. Implements the
frozen `ITranscriptStore` contract over one embedded RocksDB instance via the
`RocksDB` 11.12.0.7641 single package (managed `RocksDbSharp.dll` binding +
bundled native `rocksdb.dll`; pinned per the binding review — never the stale
`RocksDbSharp` 6.2.2 path). Correctness scope only; no tuning, no measurements
here, and the shared runner owns all verification and timing.

This directory owns: adapter project (`Urd.TranscriptHarness.Adapter.RocksDb/`),
tests (`tests/Urd.TranscriptHarness.RocksDb.Tests/`), and this README. Shared
harness files are not edited.

## Layout (declared in `Describe`)

- **One RocksDB instance**; column families per purpose:
  - `messages` — one value per message; key `(session, ordinal)` composite.
  - `sessions` — per-session metadata: key = session name, value = message
    count (kept in the same write batch as every mutation that changes it).
  - `assignments` — key = assignment id, value = full assignment record.
  - `assign-index` — key = `(target session, assignment id)`, value = status
    byte, so open assignments of a session are one prefix range scan (used by
    the deletion policies).
  - `drafts` — per-session active-stream marker (stream id + last sequence)
    plus one batch record per `StreamDraft` call, key `(session, streamId, seq)`.
- **Keys:** `[4BE utf8-name-length][name bytes][8BE big-endian ordinal/sequence]`.
  The length prefix prevents aliasing; big-endian fixed-width fields make the
  sorted keyspace the index (no separate index structure). All range operations
  seek the exact start key and re-verify the session (or stream) prefix on each
  key before consuming it.
- **Values:** explicit hand-rolled version-tagged binary encoding
  (`RecordCodec.cs`) — no reflection, no schema pipeline. Tool results are
  stored **inline** in the message value.

## Acknowledgement tier: OsFlush

Verbatim configuration recorded in `Describe()`:

- DBOptions: `SetCreateIfMissing(true)`, `SetCreateMissingColumnFamilies(true)`;
  all other DBOptions defaults (WAL enabled by default, `manual_wal_flush=false`
  default → every write is flushed from the user-space WAL buffer to the WAL
  file's buffered OS I/O on return).
- WriteOptions: default (`sync=false`) — commits are handed to the OS, never
  fsynced. This is the declared **OsFlush** tier ("handed to the OS, no fsync").
- No `Flush()`, no `CompactRange()`, no WAL-dir/flush-on-close overrides
  anywhere. `FlushWAL` is not exposed on the high-level `RocksDb` API in this
  binding (reflection-checked in the binding review) and is not used.
- **No unverified claims:** the tier declaration is based on the documented
  default write path (buffered WAL I/O, no fsync). What a process kill actually
  leaves behind is judged only by the harness kill/validate tests by content
  comparison; no power-loss guarantee is claimed.
- **Compaction / cleanup costs:** engine-default background compaction only; no
  pre-emptive tuning. `DeleteSession` (and suffix edits) write range tombstones
  whose disk reclamation happens later as engine compaction observes it —
  disk footprint before/after and compaction effects are recorded by the
  harness run pipeline as observations, not hidden and not timed here.

## Edit mechanics

- `ReplaceSuffix(fromOrdinal, newMessages)`: one atomic `WriteBatch` containing
  `DeleteRange` from the `(session, fromOrdinal)` key to the explicit sentinel
  `prefix + 0xFF×9` (exclusive upper bound; the binding rejects the unbounded
  null end) plus the reinsertions at `from..` and the updated session count.
  No stale old-suffix key is ever observable, and the delete/reinsert never
  runs as two separate commits.
- `DeleteSession(policy)`: one atomic batch — `DeleteRange` over the session's
  message keys, its metadata key, its assignment-index entries, and any draft
  records. `RequireSettled` fails while any indexed open assignment targets the
  session; `CancelOpen` flips those open assignments to `Cancelled` (records
  survive; oracle-verified) in the same batch. No dangling open references.
- Streaming: **every `StreamDraft` call writes its own persisted batch record**
  (one `WriteBatch` per call) — no buffering until finalize. `FinalizeStream`
  commits the final message insert, the session count, marker removal and the
  stream's draft-record range delete in one `WriteBatch`, so finalize is
  atomic. Declared crash policy: nothing partial is ever served; an unfinalized
  draft's partial batches may exist on disk but are never served, and a
  finalized stream is either fully inserted or absent.

## Bounded reads (no whole-session memory mirror)

- `ReadRange` / `OpenRecent` / `PageOlder` seek the composite start key and
  stop at the window end or session boundary — windows only, never a full
  session scan.
- `OpenRecentPreview` decodes each message value in "metadata mode": per part,
  kind, full-content character length (counted over UTF-8 bytes without
  decoding), tool call id, and a preview substring decoded only up to
  `previewChars` code units (whole UTF-8 clusters, exact prefix). It never
  builds full content strings of large payloads. Trade-off recorded: with
  inline storage the preview reads message blobs one row at a time; the decode
  is bounded, the row read is not (bounded payload-per-row, unbounded content
  is not decoded).
- `FetchToolResult` is the targeted single-result full-content read: exactly
  one message is read and decoded.

## Recovery (declared)

RocksDB opens with default WAL recovery: on reopen the WAL is replayed, so all
acknowledged batches are recovered. A kill mid-mutation resolves to the
before-state or the fully committed after-state (single atomic `WriteBatch`);
the harness oracle accepts exactly those outcomes for persistent tiers.

## Native dependency note

The store host's plugin loader (`AssemblyDependencyResolver` via the adapter's
`.deps.json`) resolves the managed `RocksDbSharp.dll`/`ZstdSharp.dll` assets.
The adapter additionally triggers the packaged native load
(`runtimes/win-x64/native/rocksdb.dll`) explicitly at `Open` because static
native dependency resolution via the default app context does not include a
plugin directory. In normal in-app deployments the load is already resolved and
this is a no-op.

## Tests

`tests/Urd.TranscriptHarness.RocksDb.Tests` — 15 focused facts (in-process,
temp dirs): ordered composite ranges/count metadata, suffix replacement with
reopen (no stale keys) and truncation, empty/unknown windows and bounds, the
full-catalog reopen persistence, unicode round-trip with per-char-boundary
preview checks (incl. mid-surrogate cut), bounded preview metadata + targeted
tool-result fetch, assignment lifecycle/deletion policies, streaming batch
persistence and stream-identity rules, descriptor statistics. Correctness only;
the shared runner's `smoke`/`validate` remain the cross-adapter evidence.