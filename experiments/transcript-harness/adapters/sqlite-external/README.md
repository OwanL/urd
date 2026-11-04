# sqlite-external — family 7 (SQLite metadata + external payload files)

`Urd.TranscriptHarness.Adapter.SqliteExternal.SqliteExternalTranscriptStore` —
freezing the `ITranscriptStore` + `IStoreStatistics` contract; public
parameterless constructor; net10; pinned Microsoft.Data.Sqlite 10.0.14.

## Layout

FAMILY-7 layout, as declared in `STORAGE-EXPERIMENT.md` and the assignment:
one shared SQLite database is the authority for all metadata, indexes and
edits; every part's raw TEXT content lives in one separate, IMMUTABLE payload
file addressed by a safe generated ID.

```
<storeRoot>/transcript.db (+ -wal/-shm)   metadata, indexes, previews, streams, assignments
<storeRoot>/payloads/pl-<32 hex chars>    immutable per-part payload files (raw UTF-8 TEXT)
```

- Payload IDs are **generated, not derived**: `pl-` + Guid N-format (lowercase
  hex only, filesystem-safe, no path/hostile-name concerns) and **never
  content-addressed** — identical parts get distinct files by construction; no
  dedup framework and no compression variant.
- Part rows record the pointer (`payload_id`), write-time `content_chars`, and
  a bounded preview prefix; every part row references exactly one payload file,
  uniformly (text/toolCall/toolResult/image — images store their stable
  relative-path reference, oracle-conform mapping).
- No whole-session memory mirror is kept; reads stream exactly the window's
  part rows and open only those payload files.

## Two-store ordering (declared; the designed risk, treated explicitly)

There is **no assumed cross-file transaction**: SQLite WAL covers
`transcript.db` only; the payload side is ordered, not transactional.

- **Publication**: write + OS-flush the payload file FIRST, then COMMIT the
  metadata transaction that references it. A kill in between can only leave
  an **unreferenced payload file** (never a published pointer without a
  payload — the pointer row does not exist until the payload is written).
- **Removal**: COMMIT the metadata removal (suffix edit / delete session;
  removed parts' `payload_id`s subselected before their rows go), THEN delete
  the obsolete payload files — existence-tolerant, idempotent, per-file (a
  failure or kill before deletion leaves an unreferenced file).
- **Startup orphan sweep**: `Open()` deletes every payload file not referenced
  by a committed part row, BEFORE serving anything — this reclaims both crash
  states above; no background GC, no timers.
- Consequence: the only unacknowledged persisted state is an unreferenced
  payload file. A payload **missing behind a committed pointer** is not
  producible by the store under this ordering; reads fail loudly if external
  interference ever creates one (tests include that loud-failure case).

## Bounded reads and edits

- `OpenRecentPreview` touches ONLY metadata on the default path: previews are
  write-time (C#-exact leading prefix, cap 256 UTF-16 code units; the harness
  defaults to ≤ 24 chars). `previewChars` beyond the stored cap falls back to
  reading the single referenced payload file of that one part (documented).
- `FetchToolResult` is a targeted read: one part row + its one payload file.
- `StreamDraft` = one committed persisted batch per call in SQLite (drafts
  survive reopen; identity rules: one live draft per session, cross-session
  ids rejected, identity registered by the first call even for an empty
  chunk); finalize consumes the draft in the same transaction that inserts the
  final message.
- `ReplaceSuffix` = one committed metadata transaction (delete + dense
  reinsert; shorter replacement truncates; out-of-range edit points throw);
  obsolete payload files are removed after the commit.

## Acknowledgement and recovery limits (declared honestly)

- Every mutation COMMITs its one metadata transaction before the call returns
  (after its payload writes); `journal_mode=WAL; synchronous=NORMAL` hands
  committed data to the OS without an fsync — the OS-flush ack tier.
- **No power-loss or fsync-durability claim** for either file; SQLite WAL
  gives process-crash recovery for the metadata: acknowledged records replay
  identically; an unacknowledged mid-transaction mutation persists as
  before-state or fully committed after-state (payload files present, or
  unreferenced until the sweep); recovery never invents or reorders records.
- Metadata (sessions, parts, assignments, stream state) is SQLite-only and
  never mirrored in memory across calls.

## Commands

```
dotnet build Urd.TranscriptHarness.Adapter.SqliteExternal/Urd.TranscriptHarness.Adapter.SqliteExternal.csproj -c Release -p:BuildProjectReferences=false
dotnet build tests/Urd.TranscriptHarness.Adapter.SqliteExternal.Tests/Urd.TranscriptHarness.Adapter.SqliteExternal.Tests.csproj -c Release -p:BuildProjectReferences=false
dotnet run --project experiments/transcript-harness/adapters/sqlite-external/tests/Urd.TranscriptHarness.Adapter.SqliteExternal.Tests -c Release -p:BuildProjectReferences=false
dotnet run --project experiments/transcript-harness/runner/Urd.TranscriptHarness.Runner -c Release -- smoke --store-assembly <adapter dll> --store-type Urd.TranscriptHarness.Adapter.SqliteExternal.SqliteExternalTranscriptStore
dotnet run --project experiments/transcript-harness/runner/Urd.TranscriptHarness.Runner -c Release -- validate --store-assembly <adapter dll> --store-type Urd.TranscriptHarness.Adapter.SqliteExternal.SqliteExternalTranscriptStore
```

Fixtures are synthetic and live in OS temp only.