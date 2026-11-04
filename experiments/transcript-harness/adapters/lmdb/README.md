# Family 4 — LMDB indexed records (Stage 0 adapter)

Owner: this directory only (`adapters/lmdb/`); shared harness and other
families are untouched. `STORAGE-EXPERIMENT.md` owns the programme; the frozen
contract is `ITranscriptStore` (+ optional `IStoreStatistics`).

## Projects

| Project | Kind | Notes |
| --- | --- | --- |
| `Urd.TranscriptHarness.LmdbStore/` | net10.0 classlib, plugin | `LightningDB` **0.23.1** (exact; LMDB 1.0 format, bundled native `lmdb.dll`); `EnableDynamicLoading`+`CopyLocalLockFileAssemblies` so runtime assets ship beside the adapter DLL for path-based plugin loading |
| `Urd.TranscriptHarness.LmdbStore.Tests/` | net10.0 console | adapter-owned focused correctness suite (no test framework dependency) |

## Design decisions (record, not a selection)

- **Binary record codec: a simple defined codec, not MessagePack.** Values are
  versioned big-endian layouts (`StoreCodec.cs`, layout documented in its
  header comment); keys use a defined order-preserving encoding
  (`KeyCodec.cs`). Reasons: (a) the preview path must be able to read part
  metadata without touching huge payloads — the split layout below does that
  at the record level, which MessagePack's whole-record decode does not offer
  for free; (b) zero extra packages; codec is small and fully unit-covered.
  No lone-surrogate strings are supported by the UTF-8 content encoding
  (the harness generator does not produce them).
- **Ordered composite keys are the index** — no custom index and no
  live-top-generation machinery on top of LMDB's sorted keyspace.
- **Split header/content records.** `msgs` keys carry a small header record
  (role, per-part kind, full-content char length, toolCallId, leading
  256-char preview prefix; image parts inline since they are references
  only). Full text/args/result content lives in a parallel `content` record
  keyed `(session, ordinal, partIndex)`. `OpenRecentPreview` therefore never
  decodes a large tool result; only when a caller asks for a preview longer
  than the 256-char stored budget does the adapter do a targeted content
  fetch (rare; `FetchToolResult` is always a targeted single-key read).
- **Key encoding.** Session/assignment/stream id strings are encoded as
  UTF-16-unit triplets `01 hi lo` with a `00` terminator: LMDB byte order then
  equals `StringComparer.Ordinal` order, one session's keys are contiguous
  and prefix-free (no session encoding is a byte-prefix of another — proven in
  `KeyCodec.cs`), ordinals are 8-byte big-endian, part indices 4-byte.
  Ordinals are dense `0..count-1`, maintained per session in the `sessions`
  DBI, exactly per the contract.
- **Transactions.** One write transaction per mutation operation:
  `AppendMessage`, `StreamDraft` (each call commits one chunk row + meta row —
  no unbounded buffering), `FinalizeStream` (deletes draft rows + inserts the
  message + bumps count in one commit), `ReplaceSuffix` (cursor range-deletes
  the replaced `(session, ordinal≥from)` rows and the content rows, then
  reinserts, atomically — old suffix keys are never observable after an
  edit), `CreateAssignment`/`SettleAssignment` (id record + derived target
  index entry together), `DeleteSession` (message/content/draft rows +
  assignment resolution + meta entry in one commit).
- **Reads go to the map; there is no in-memory mirror of transcript content.**
  Bounded-window reads are key-range reads; preview is header-only.
- **Durability tier: `FsyncOrJournal`.** LMDB default env flags (commit
  fsync enabled; no `NOSYNC`/`NOMETASYNC`/`WRITEMAP`); nothing weakened.
  No OS-flush config was added: default sync is the stronger accepted tier,
  and weaker flags were not preset on guesswork (would need measured
  justification, and similar timings would not make a weaker flag the same
  tier).
- **Map size: 1 GiB address reservation (`MapSize`), not resident memory.**
  Chosen to give headroom for later stress tiers around ≥ 200 MiB logical
  payload plus LMDB write-multiply/free-page behaviour, without reserving the
  whole disk. Measured: after ~800 messages with 64 KiB tool results
  (~17 MiB payload) the on-disk `data.mdb` is ≈ 17 MiB and `lock.mdb` 8 KiB —
  the data file grows lazily; the reservation is address space. Windows map
  growth is closed-state-only (LMDB documented limitation), so the fixed
  reservation is the adapter's declared bound; ~1 GiB of encoded data in one
  environment would need an owner decision to enlarge. LMDB's default
  511-byte key limit bounds id lengths (~155 chars for session ids); harness
  scale ids are far below.
- **Kill/recovery semantics.** Recovery = LMDB single-writer transactional
  atomicity on reopen; unacknowledged commits may persist or not (before/after
  acceptance is the shared oracle's job). Not self-tested here with kills;
  the shared host/runner validation owns crash runs at this tier.

## Verification (local, OS-temp data only)

```shell
dotnet build adapters/lmdb/Urd.TranscriptHarness.LmdbStore -c Release -p:BuildProjectReferences=false
dotnet build adapters/lmdb/Urd.TranscriptHarness.LmdbStore.Tests -c Release -p:BuildProjectReferences=false
dotnet run  --project adapters/lmdb/Urd.TranscriptHarness.LmdbStore.Tests -c Release --no-build
# shared smoke with the prebuilt runner and this adapter as plugin:
runner/exe smoke --store-assembly <adapter dll> --store-type Urd.TranscriptHarness.LmdbStore.LmdbTranscriptStore
```

Focused suite: 11 cases, all pass — round trips (text/tool call+result/image),
read bounds and validation, preview bounded on an >8 MiB tool result (window
allocation < 2 MiB, prefix exactness, targeted fetch identical), preview
beyond the stored budget, suffix edit renumbering/truncation/empty, Unicode
session ordering vs `StringComparer.Ordinal` (incl. astral, `€`, embedded
U+0000) and sibling-block isolation, assignment lifecycle invariants, delete
invariants, draft persistence across reopen, fresh-instance reopen, and a
mixed-scale store (one 800-message history next to small sessions). Plugin
loader works with the current host as-is: shared smoke passed (634 ops,
10 checkpoints; report at
`%TEMP%/urd-lmdb-adapter-smoke/run-a/report.json`).

## Known risks / bounded behaviour

- LMDB is single-writer; concurrent multi-writer behaviour is Stage 2 scope,
  not exercised here.
- `LightningDB` is a 0.x wrapper and this pins the LMDB 1.0 data format
  (incompatible with 0.9.x dumps — deliberate, per the binding review).
- Map resize requires a closed environment on Windows (documented LMDB
  limitation); the fixed generous reservation is the trade.
- Same-stream-id reuse across *different* sessions is not globally rejected
  (the contract requires only per-session single-active-stream checking;
  the harness generator uses unique stream ids).
- Plugin-loading behaviour depends on the shared host; if the host's loader
  changes, only the two csproj plugin properties above may need review —
  adapter source has no loader-coupled code.