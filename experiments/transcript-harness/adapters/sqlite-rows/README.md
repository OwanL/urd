# SqliteRows adapter — family 1 (shared SQLite, row-per-message)

Stage-0 adapter for the transcript-storage experiment programme
([STORAGE-EXPERIMENT.md](../../../STORAGE-EXPERIMENT.md)). One shared SQLite
database for every session, one row per message, `(session_id, ordinal)`
primary key as the ordered index, `Microsoft.Data.Sqlite` **10.0.14** on
net10.0. Implements the frozen `ITranscriptStore` (+ `IStoreStatistics`) and
nothing else; all other code is shared-harness owned.

## Design record (declared semantics)

**Layout.** One database file `transcript.db` (WAL/SHM companions next to it) in
the store root:

| Table | Content |
| --- | --- |
| `sessions` | Session-existence row only (`session_id`). Created on the first message insert. |
| `messages` | One row per message: `role` + `parts_json` (the ONLY content storage). PK `(session_id, ordinal)` — the ordered access path for all windows. |
| `parts_meta` | DERIVED queryable part metadata: `kind`, `content_chars`, `tool_call_id`, `preview` (bounded leading prefix ≤ 256 chars), `bounded` flag. Never stores full content. |
| `streams` | Active draft streams: `stream_id` PK, `session_id` (unique — one live draft per session), `draft_text` (accumulated committed batches). |
| `assignments` | Bounded assignment records: id, target session, title, status (0 open / 1 settled / 2 cancelled), result_text. |

Counts are derived from the message table (`LEFT JOIN … COUNT`), so the
session index can never drift from the message rows; zero-message sessions
(truncated by an edit) stay listed, matching the reference store.

**Content representation (explicit, and different from the block design).**
Raw text + JSON only — no binary encoding, no separate raw-content columns.
Every part's full content lives inside the message row's `parts_json` envelope
(`PartsCodec`): kind-discriminated JSON text entries `text / toolCall /
toolResult / image`. Large tool results are **inline** message-row content; no
spill table (that is a family-2-style choice and is deliberately NOT this
baseline). There are no content-block rows: message rows are what differ from
family 2, where large text is separately addressable block storage.

**Preview metadata design (explicit).** `parts_meta` is derived from the
envelope at write time. `OpenRecentPreview` selects the latest message headers
and LEFT JOINs metadata, retaining messages with zero parts. Its cursor comes
from the returned window, not a separate count. A short deferred read transaction
keeps metadata and any fallback reads in one snapshot across concurrent commits.

Prefixes use the shared `PreviewText.Prefix` rule: the **longest leading prefix**
of at most `max(0, previewChars)` UTF-16 code units without splitting a valid
surrogate pair. `ContentChars` still counts UTF-16 code units, not Unicode scalars
or grapheme clusters. At a split pair the prefix is one code unit shorter; it
never inserts a replacement character for valid input.

Stored prefixes are capped at `PreviewStoredPrefix = 256`. Requests ≤256 use
only metadata and message headers, **never `parts_json`**, even when the stored
prefix is 255 because of a boundary pair. Larger requests can decode each flagged
message's **entire JSON envelope once**, not merely the requested part; they are
not payload-byte-bounded. Smoke uses ≤24, so it takes the metadata path.

**Edit mechanics.** `ReplaceSuffix(k, new)`: one committed transaction —
`DELETE FROM messages/parts_meta WHERE session=? AND ordinal>=k` then
reinsertion of the replacement rows renumbered densely from `k`. Classic suffix
delete+insert; a shorter replacement truncates; `k == count` is a pure append;
the replaced suffix's payloads and derived `parts_meta` rows are removed
together. History replacement only — never rollback.

**Streaming.** Gen chunks vs persistence batches are distinct: every
`StreamDraft` call is exactly ONE committed store write batch (append into the
`streams` row; identity registered even for an empty first batch). Nothing
partial is ever served: drafts never appear in message history or any read
window. `FinalizeStream` commits the final message insert + draft removal in
ONE transaction; the draft text survives until then. Rules: one live draft per
session (different id while active → error); a stream id still active in
another session is rejected (a stream id names one live draft); finalize of an
unknown/mismatched draft errors. After finalize the id is free again.

**Acknowledgement boundary (declared, verbatim configuration).**
`journal_mode=WAL; synchronous=NORMAL; wal_autocheckpoint=1000 (SQLite
default)`. Every mutation is one transaction `COMMIT`-ed before the call
returns. WAL + NORMAL hands completed data to the OS without an fsync →
declared tier is **`OsFlush`**. **No power-loss or fsync-durability claim is
made**, including from any kill test result.

**Recovery rules (declared).** Ordinary reopen replays the WAL/rolls back any
torn commit: a record is either absent or complete; each acknowledged
(committed) record replays identically after reopen; recovery never invents,
reorders or re-executes records. An unacknowledged mid-transaction mutation may
persist as the full before-state or the fully committed after-state. Active
drafts are persisted per batch, so a reopen mid-stream finds the draft intact
and it still serves nothing partial.

**Deletion / assignments (per shared contract).** `DeleteSession` is final
closure: payloads, derived metadata, drafts and the session row removed
atomically; under `RequireSettled` it fails while any assignment targeting the
session is open; under `CancelOpen` the open assignments are set to
`Cancelled` in the SAME transaction. Settled/cancelled assignments remain as
assignment history — no dangling OPEN references can remain.

## Read semantics (contract/README authority)

- Unknown or empty sessions return empty windows for `OpenRecent`, `PageOlder`,
  `ReadRange`, `OpenRecentPreview` (absence is not an error).
- `PageOlder` clamps `before` and the window to the real history (reference
  parity). `ReadRange` on a KNOWN session validates strictly and throws for
  out-of-range windows (reference parity).
- `AppendMessage`/`FinalizeStream` create their target session;
  `ReplaceSuffix`/`DeleteSession`/`FetchToolResult` require an existing one.
- `ListSessionsMeta` is ordered by `session_id` (SQLite BINARY order); the
  shared verification re-sorts logically, so store order is not a contract.

## Shared-contract notes

The earlier `ReadRange`/`PageOlder` mismatch reports are stale: the shared executor
now enforces strict range bounds and clamps `before` before subtraction.
Cross-session active stream-ID reuse still differs: the reference store can
accumulate a batch into another session's draft; this adapter rejects it. Current
traces avoid that collision. The 2026-11-04 preview follow-up clarified Unicode
prefix semantics in the shared contract/reference/oracle; other adapters have
not been updated or qualified at split-surrogate boundaries.

## Build and use

Shared harness must be built first (owner's step; this directory never builds
or edits shared projects):

```shell
dotnet build experiments/transcript-harness/Urd.TranscriptHarness.sln -c Release
```

This adapter, against the pre-built shared contracts only:

```shell
dotnet build experiments/transcript-harness/adapters/sqlite-rows/Urd.TranscriptHarness.Adapter.SqliteRows/Urd.TranscriptHarness.Adapter.SqliteRows.csproj -c Release -p:BuildProjectReferences=false
dotnet build experiments/transcript-harness/adapters/sqlite-rows/tests/Urd.TranscriptHarness.SqliteRows.Tests/Urd.TranscriptHarness.SqliteRows.Tests.csproj -c Release
experiments/transcript-harness/adapters/sqlite-rows/tests/Urd.TranscriptHarness.SqliteRows.Tests/bin/Release/net10.0/Urd.TranscriptHarness.SqliteRows.Tests.exe
```

Shared smoke (OS-flush ack, real child-store-host IPC, oracle verification):

```shell
experiments/transcript-harness/runner/Urd.TranscriptHarness.Runner/bin/Release/net10.0/Urd.TranscriptHarness.Runner.exe smoke \
  --store-assembly <abs path to>\Urd.TranscriptHarness.Adapter.SqliteRows.dll \
  --store-type Urd.TranscriptHarness.Adapter.SqliteRows.SqliteRowsTranscriptStore
```

Public plugin type: `Urd.TranscriptHarness.Adapter.SqliteRows.SqliteRowsTranscriptStore`
(public, parameterless constructor). The csproj sets
`EnableDynamicLoading=true` + `CopyLocalLockFileAssemblies=true`; the adapter's
bin directory carries its `.deps.json` and the `runtimes/` native tree.
Loading is adapter-owned (`NativeDependency`): an `AssemblyDependencyResolver`
built from the adapter's deps.json resolves the managed `SQLitePCLRaw.*`/`Microsoft.Data.Sqlite`
dependencies, a `NativeLibrary.SetDllImportResolver` on
`SQLitePCLRaw.provider.e_sqlite3` resolves the native `e_sqlite3`, and
`SQLitePCL.Batteries_V2.Init()` sets the provider explicitly (the bundle's
module initializer does not fire in a LoadFrom-hosted scenario). The shared
host needed no changes for this adapter.

## Known gaps / notes

- `synchronous=NORMAL` in WAL mode is the documented OS-flush configuration;
  durability beyond process crash (power loss) is NOT claimed or tested here.
- Preview fallback for `previewChars > 256` decodes the entire flagged message
  envelope. Only the default ≤256 path is payload-free.
- Existing experimental databases with already-corrupted stored prefixes are
  not repaired by this change; use fresh fixtures. No migration was added.
- Lone surrogates in content are representable in C# strings but not safely in
  the JSON envelope (System.Text.Json behavior); harness-generated content is
  valid UTF-16, so this is a documented limitation, not a trace-visible gap.
- No schema version table yet (Stage 0); reopen assumes the same layout. A
  `meta` table for schema versioning is trivial to add if the programme wants
  in-place evolution.
- The shared host/trace is serial. Focused preview regressions inject commits
  through a second WAL connection using reader-local TEMP views/functions;
  no sleeps or production hooks. They cover append/truncate, replacement and
  deletion during reads, including fallback after the metadata reader is done.
  This is snapshot correctness evidence, not a load/fairness/soak benchmark.
- `PageOlder` and `ReadRange` still make separate existence/count/window reads
  without a shared snapshot. Concurrent edits can invalidate their bounds;
  these full-read races are outside this preview-only fix.
- Windows-only at this stage (programme contract; RID-specific natives are
  restored for other OSes but unexercised).

## Dependencies / footprint

- `Microsoft.Data.Sqlite` 10.0.14 (transitive `SQLitePCLRaw.*` 2.1.13; bundled
  native `e_sqlite3` loaded from the adapter's `runtimes/win-x64/native`).
- No other packages. Tests consume pre-built adapter, contracts, reference and
  core DLLs by HintPath (no shared-project builds) and the same SQLite binding.
  Unicode regressions check independent expected strings, reopen, the real
  shared JSON codec and oracle, plus the payload-free stored-boundary path.