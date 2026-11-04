# SQLiteBlocks adapter — family 2 (shared SQLite, content blocks)

Stage-0 adapter for the transcript-storage experiment programme
([STORAGE-EXPERIMENT.md](../../../STORAGE-EXPERIMENT.md)). One shared SQLite
database for every session; message rows are headers only and every part's
raw TEXT content lives in a separately addressable `blocks` row referenced by
its part row. `Microsoft.Data.Sqlite` **10.0.14** on net10.0. Implements the
frozen `ITranscriptStore` (+ `IStoreStatistics`) and nothing else; all other
code is shared-harness owned.

## Design record (declared semantics)

**Layout.** One database file `transcript.db` (WAL/SHM companions next to it)
in the store root:

| Table | Content |
| --- | --- |
| `sessions` | Session-existence row only (`session_id`). Created on the first message insert; zero-message sessions (truncated edits) stay listed, matching the reference store. |
| `messages` | Header rows only: `(session_id, ordinal)` PK + `role`. NO content — this is where family 2 differs from family 1. |
| `parts` | One row per part: `kind` (text/toolCall/toolResult/image), `tool_call_id`, `tool_name`, `image_id`, `block_id` (the content reference), `content_chars` (full-content character length, write-time), `preview` (bounded leading prefix) + `bounded` flag. This is the message-to-block reference table. |
| `blocks` | Separately addressable raw TEXT content rows: `block_id` PK (`blk:` + session + `:` + ordinal + `:` + partIndex), `content`. One-to-one with part rows — no inline/small-part special case, so reference ownership is trivially countable. |
| `streams` | Active draft streams: `stream_id` PK, `session_id` (unique), `draft_text` (accumulated committed batches). |
| `assignments` | Bounded assignment records: id, target session, title, status (0 open / 1 settled / 2 cancelled), result_text. |

**Content representation.** Raw TEXT content rows only — no new binary
encoding variant. Every part's content string (text content, tool-call args
JSON, tool-result content, image relative path) is one `blocks` row; part rows
hold plain metadata columns. Reconstructing a message is a three-table join
(messages → parts → blocks) ordered by `(ordinal, part_index)` — no JSON
decoding anywhere in the read path.

**Block-reference invariants and orphan cleanup (explicit).**
1. Every part row references exactly one block row (one-to-one).
2. Blocks are created strictly inside the same committed transaction as the
   part rows that reference them — no committed block is ever unreferenced.
3. Any mutation removing part rows first deletes exactly those parts' blocks
   (primary-key subselect) inside the SAME transaction: `ReplaceSuffix`
   removes the replaced suffix's blocks before reinserting; `DeleteSession`
   removes the session's blocks in final closure. No background GC, no
   reclaim timer; after every committed mutation, `blocks` holds exactly the
   live parts' content).

**Preview metadata design.** Previews are derived at write time into part rows
(C#-exact leading prefix, capped at `PreviewStoredPrefix = 256` UTF-16 code
units, matching the oracle's string slicing). `OpenRecentPreview` touches only
message headers + part rows on the default path — block content is never
opened. A request with `previewChars` beyond the stored cap falls back to
reading the single referenced block of that one part (the only preview path
that touches content text, documented; the harness requests ≤ 24 chars so the
metadata path covers the workload).
`FetchToolResult` is a targeted two-row read (one part row + its `blocks` row),
never a window.

**Edit mechanics.** `ReplaceSuffix(k, new)`: one committed transaction —
delete the suffix's blocks (via parts), the parts, and the message headers,
then reinsert densely renumbered rows (message headers, parts, blocks) from
`k`. Classic suffix delete+insert; shorter replacement truncates; `k == count`
is a pure append. History replacement only — never rollback.

**Streaming.** Every `StreamDraft` call is exactly ONE committed store write
batch (identity registered even for an empty first batch; one live draft per
session; a live streamId may not move sessions). `FinalizeStream` commits the
final message (headers + parts + blocks) and removes the draft in one
transaction — nothing partial is ever served as message content.

**Assignments/deletes.** Assignment records are bounded rows. `DeleteSession`
enforces the invariant in one transaction (`RequireSettled` fails while open
assignments target the session; `CancelOpen` cancels them in the same atomic
operation), then final-closes messages, parts, blocks, drafts and the session
row. Settled/cancelled records survive as history.

## Durability (declared AckTier: `OsFlush`)

Every mutation runs one transaction and COMMITs before the call returns.
Database settings, verbatim: `journal_mode=WAL`, `synchronous=NORMAL`,
`wal_autocheckpoint=1000` (SQLite default) — a commit hands completed data to
the OS through buffered file I/O without an fsync. **No power-loss or
fsync-durability claim.** Recovery: ordinary reopen replays the WAL / rolls
back torn transactions; every acknowledged (committed) record replays
identically; an unacknowledged (mid-transaction) mutation persists as the
full before-state or the fully committed after-state; recovery never invents
or reorders records. Drafts are persisted per-batch, so a reopen with an
unfinished stream finds it intact — and it still never serves content.

## Build / test / harness

```shell
dotnet build experiments/transcript-harness/adapters/sqlite-blocks/Urd.TranscriptHarness.Adapter.SqliteBlocks/Urd.TranscriptHarness.Adapter.SqliteBlocks.csproj -c Release -p:BuildProjectReferences=false
dotnet run  --project experiments/transcript-harness/adapters/sqlite-blocks/tests/Urd.TranscriptHarness.Adapter.SqliteBlocks.Tests -c Release -p:BuildProjectReferences=false
dotnet run --project experiments/transcript-harness/runner/Urd.TranscriptHarness.Runner -c Release -- smoke --store-assembly <adapter dll> --store-type Urd.TranscriptHarness.Adapter.SqliteBlocks.SqliteBlocksTranscriptStore
dotnet run --project experiments/transcript-harness/runner/Urd.TranscriptHarness.Runner -c Release -- validate --store-assembly <adapter dll> --store-type Urd.TranscriptHarness.Adapter.SqliteBlocks.SqliteBlocksTranscriptStore
```

Public parameterless constructor; plugin dependency resolution via the shared
host's AssemblyDependencyResolver plus the adapter's own minimal SQLitePCLRaw
provider wiring (`NativeDependency.EnsureResolved()` — the only explicit
battery init; no own plugin framework). Fixtures are synthetic and live in
OS temp only. LOC (measured, adapter vs tests): see the runner's report
section; soft budget honoured (~1.5k total owned LOC incl. tests + README).