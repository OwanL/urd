# Urd.TranscriptHarness.LiteDbStore

Family 3 transcript adapter: **LiteDB v5 documents**, pinned to LiteDB
**5.0.21** (`net10.0`). Implements the frozen
`ITranscriptStore` + `IStoreStatistics` contracts with a public
parameterless constructor; the shared host loads it via `Assembly.LoadFrom`
(a static `AssemblyResolve` hook resolves `LiteDB.dll` from the adapter's own
output directory since `LoadFrom` does not consume `.deps.json`).

## Store layout

Single LiteDB file `<storeRoot>/transcripts.db` (plus LiteDB's `<file>-log`
WAL companion while uncheckpointed pages exist; folded away on `Dispose`).
Opened with a mapper that sets `TrimWhitespace = false` (v5's default mapper
otherwise trims leading/trailing whitespace off stored strings, corrupting
transcript content). Collections (BSON, engine-implicit encoding):

| name  | doc shape | purpose |
|-------|-----------|---------|
| `msg` | one document per message (`_id` = ordered key `session \x1F <ordinal:D19>`) | messages with all part kinds; tool-call name/ids and full content inline or spilled |
| `blk` | `_id` = `msg key \x1F <part:D4>`, plus `S` session field | oversized part content (≥ 16 000 chars), individually retrievable — the `FetchToolResult` target |
| `draft` | one document **per `StreamDraft` call** | persisted stream batches (never deferred to finalize) |
| `asg` | `_id` = assignment id | assignments |
| `ses` | `_id` = session id, count | tiny transactionally-maintained session-count index so `ListSessionsMeta` never decodes message documents |

Ordered keyspace: fixed-width D19 ordinals make lexicographic order equal
numeric order for a session’s own keys, so every (session, ordinal) window is
a B-Tree range scan on the primary index. LiteDB v5 sorts string indexes with
its default culture collation (`en-NZ/IgnoreCase`), which does not agree with
ordinal bytes: distinct session ids that only differ in case — or in embedded
separator/control characters (e.g. `ab` vs `a\x1Fb`) — collide in the unique
`_id` index (insert rejected), and prefix-ambiguous ids (`session-1` vs
`session-10`) leak keys across ranges. The adapter therefore enforces the
engine's byte-ordinal collation at `Open` (`RebuildOptions` with
`Collation.Binary`; the `COLLATION` pragma is read-only once created, so the
first open of an existing non-ordinal database performs a one-time
content-preserving rebuild that recollates all indexes, and later opens see
the persisted pragma and skip it). Under byte-ordinal collation the key
design is exact: distinct session ids never produce collation-equal keys and
ranges never cross sessions. As belt-and-braces, every range scan and range
delete also carries an `S = sessionId` equality filter. Previews use
a write-time stored prefix (cap 4096 chars/part) and never decode `blk`
documents; the harness oracle requests 24 chars. Range/`OpenRecent` reads may
decode full content per reference semantics.

## Mutations

Every mutation is one explicit LiteDB transaction (`BeginTrans`/`Commit`/
`Rollback`; v5 transactions are thread-scoped, hence no transaction object).
`StreamDraft` persists exactly one batch document per call. `FinalizeStream`
(deletes the stream's batch docs + appends the message) and `ReplaceSuffix`
(deletes the suffix and its spilled blocks, reinserts at explicit ordinals,
and updates the count) commit atomically, so no old-ordinal keys are ever
exposed. `DeleteSession` enforces the assignment invariant (`RequireSettled`
blocks; `CancelOpen` cancels open assignments in the same transaction).

## Durability (declared AckTier: `OsFlush`)

Evidenced from the pinned v5.0.21 source: commit writes WAL log pages
synchronously to the OS (`DiskService.WriteLogDisk`, "sync mode"; FileStream
created with a one-page 8 KiB buffer), and **no fsync/FlushToDisk exists in
the commit path**. Acks therefore mean OS-visible bytes — process-kill safe,
not power-loss durable. Recovery at open replays confirmed WAL transactions
(`WalIndexService.RestoreIndex`) and discards never-confirmed pages, so a
mid-commit kill resolves to the before- or after-state, never a partial
message. Default pragmas (direct connection, journal on, checkpoint 1000
pages); `Dispose` runs `Checkpoint()` (clean close folds the log).

## Build / test

```
dotnet build experiments/transcript-harness/adapters/litedb/Urd.TranscriptHarness.LiteDbStore/Urd.TranscriptHarness.LiteDbStore.csproj -c Release -p:BuildProjectReferences=false
dotnet experiments/transcript-harness/adapters/litedb/Urd.TranscriptHarness.LiteDbStore.Tests/bin/Release/net10.0/Urd.TranscriptHarness.LiteDbStore.Tests.dll
```

Focused checks (51): kill-child WAL replay without `Dispose`, reopen equality,
all part kinds + Unicode, large-result spill/preview/fetch, draft batch rules,
suffix replacement incl. truncation and spilled content inside replacements,
window bounds, unknown-session semantics, assignment invariants across
reopen, statistics.

Shared smoke (end-to-end oracle incl. mid-trace kill):

```
dotnet run --project experiments/transcript-harness/runner/Urd.TranscriptHarness.Runner -c Release -- smoke \
  --store-assembly experiments/transcript-harness/adapters/litedb/Urd.TranscriptHarness.LiteDbStore/bin/Release/net10.0/Urd.TranscriptHarness.LiteDbStore.dll \
  --store-type Urd.TranscriptHarness.LiteDbStore.LiteDbTranscriptStore
```