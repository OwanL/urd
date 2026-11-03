# Transcript Storage Experiment Harness

Shared runner programme for comparing transcript-storage candidates under one
frozen adapter contract. Programme background lives in
[../../STORAGE-EXPERIMENT.md](../../STORAGE-EXPERIMENT.md) (with the binding
review in [../../STORAGE-BINDING-REVIEW.md](../../STORAGE-BINDING-REVIEW.md));
this README documents the harness code in `experiments/transcript-harness/`.

Status: **Stage 0 smoke + self-tests complete; no real storage engine adapter
yet.** The only shipped adapter is the in-memory reference store, used to prove
the runner mechanics, generation, protocol and oracle. No result derived from it
may be treated as evidence about any real storage family, and nothing here
claims durability.

## Layout

| Path | Project | Purpose |
| --- | --- | --- |
| `contracts/` | Urd.TranscriptHarness.Contracts | Frozen adapter contract (`ITranscriptStore`, `IStoreStatistics`, record/policy types). |
| `core/` | Urd.TranscriptHarness.Core | Deterministic trace generator, oracle model + verification, trace executor, JSON codec, IPC protocol + host server, child-process adapter, stats, path helpers. |
| `reference/` | Urd.TranscriptHarness.ReferenceStore | In-memory reference adapter (correctness baseline; `AckTier.MemoryOnly`, never durability evidence). |
| `host/` | Urd.TranscriptHarness.StoreHost | Store-host child process. Loads the default built-in reference store or any adapter by explicit `--store-assembly` + `--store-type`; adding an adapter never edits shared host files. |
| `runner/` | Urd.TranscriptHarness.Runner | Shared runner CLI: `selftest`, `smoke`. Owns traces, verification and measurement. |

## Contract (frozen for this milestone)

Adapter responsibilities are exactly `ITranscriptStore` (+ optional
`IStoreStatistics`); the runner owns everything else. Key frozen semantics, all
verified by the oracle:

- **Ordinals are dense** 0..count-1 per session; `AppendMessage` assigns the
  tail position. `ReplaceSuffix(fromOrdinal, newMessages)` keeps
  `[0, fromOrdinal)`, removes the suffix and renumbers newMessages densely; a
  shorter replacement truncates. History replacement only — never rollback.
- **Reads on unknown or empty sessions return an empty window** (absence is not
  an error). Mutations create their target session; `DeleteSession` must name a
  real one.
- **Deletes final-closure** under the assignment invariant: `RequireSettled`
  must fail while any assignment targeting the session is open;
  `CancelOpen` settles them as part of the same logical operation. Dangling
  references to deleted sessions are correctness failures, never quirks.
- **Streaming separates generation chunks from persistence batches:** every
  `StreamDraft` call is one incremental store write batch for an active draft
  stream; a different `streamId` while one is active is an error; nothing
  partial is ever served. `FinalizeStream` marks completion and inserts the
  final message. (Explicitly so stores cannot defer all writes to finalize and
  win streaming comparisons by buffering.)
- **`FetchToolResult`** is the only full-content tool-result read path
  (hover/inspection).
- **`OpenRecentPreview(sessionId, count, previewChars)`** is the bounded
  metadata/preview window for chat-list/inspector reads: per part it returns
  kind, full-content character length, toolCallId, and a preview capped at
  `previewChars` — never full payload text (a contract violation). Full-result
  layouts are demonstrated through preview + `FetchToolResult`, not by
  materializing full ranges.
- **Ack tiers** (`MemoryOnly`, `OsFlush`, `FsyncOrJournal`) are declared by the
  adapter's `Describe()` and recorded, never set or guessed by the harness.

## Self-tests

`selftest` covers: generator determinism, oracle fault detection (reversed
order, altered content, wrong counts, dropped records, wrong tool-result
content, lost sessions), full catalog round trip, streaming/finalize rules,
preview-window rules (metadata-only + bounded), suffix-edit renumbering and
truncation, delete/assignment invariants, child-process lifecycle
(ready/ack/read/close), and kill mechanics. All pass against the reference
adapter.

**Kill tests against the memory-only reference store validate harness mechanics
only.** Crash acceptance by before/after content comparison activates with the
first real persistent adapter (see `Oracle.VerifyAfterCrash`, which reports false
and structural-coherence-only for `AckTier.MemoryOnly`).

## Fixture tiers and provenance

- Current milestone: **smoke scale only** (few sessions, kilobyte-scale text).
  No huge fixtures are generated, and no real session content is read.
- Planned later tiers: standard (~20 MiB large transcript plus small sessions,
  10k–100k-record histories, tool results from kilobytes to several hundred KiB,
  mixed with many small sessions in one store instance) and a configurable
  **huge stress tier (≥ 200 MiB / 1 GiB logical payload)** with the oracle
  isolated, per `STORAGE-EXPERIMENT.md`.
- Scale justification provenance (aggregate only, retained nowhere): the
  programme parent performed a **metadata-only census** of the local Pie
  session directory — 902 session files, total ≈ 7.1 GiB, median ≈ 1.31 MiB,
  p90 ≈ 19.22 MiB, max ≈ 184.44 MiB. Encoded envelope sizes, not semantic
  payload sizes; used only to justify tier sizing.
- Real user-session material remains optional and constrained (sanitized local
  copies, temp only, never committed) and can only be imported later under the
  approved metadata-only/owner-approved task; the synthetic deterministic
  generator is always the fallback and exists first.

## Measurement rules

- Latency: named statistics only (min/p10/median/p90/max + sample count) —
  no invented percentiles, no aggregate score.
- Memory/resources reported separately, never merged: managed allocations
  (`IStoreStatistics` where present), child private bytes and working set
  (sampled), store disk footprint (bytes + file count).
- Adapter LOC vs harness LOC reported separately, alongside dependency and
  install-footprint cost; small-code compliance is measured, not asserted.
- Stdout summaries are convenience output; the machine-readable evidence is the
  run's `report.json` in the owned temp run directory
  (`%TEMP%/urd-transcript-harness/run-<timestamp>-<id>/`).

## Commands

.NET 10 SDK (verified with 10.0.403). Restore is scoped (no package feeds
required in this milestone).

```shell
dotnet build experiments/transcript-harness/Urd.TranscriptHarness.sln -c Release
dotnet run --project experiments/transcript-harness/runner/Urd.TranscriptHarness.Runner -c Release -- selftest
dotnet run --project experiments/transcript-harness/runner/Urd.TranscriptHarness.Runner -c Release -- smoke
```

`smoke` accepts overrides: `--seed`, `--sessions`, `--messages-min`,
`--messages-max`, `--medium-kb`, `--medium-count`, `--large-kb`,
`--store-assembly <dll>` + `--store-type <FullTypeName>` for a real adapter,
and `--temp-root <dir>`. Large tiers are configuration-only; nothing huge is
generated by default.

## Deferred until real adapters

- Kill/crash acceptance verdicts by content comparison (persistent tiers only).
- Attachment files with multiple large-image references per session.
- Cleanup-action footprints (vacuum/compaction timings).
- Stage 1 samples with a shared default OS-flush ack tier across candidates.