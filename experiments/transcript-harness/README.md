# Transcript Storage Experiment Harness

Shared runner programme for comparing transcript-storage candidates under one
frozen adapter contract. Programme background lives in
[../../STORAGE-EXPERIMENT.md](../../STORAGE-EXPERIMENT.md) (with the binding
review in [../../STORAGE-BINDING-REVIEW.md](../../STORAGE-BINDING-REVIEW.md));
this README documents the harness code in `experiments/transcript-harness/`.

Status: **Stage 0 qualification in progress; no comparative performance verdict.**
SQLite rows, LiteDB, LMDB and RocksDB adapters are implemented under `adapters/`
and have passed focused tests and shared smoke workloads. Additional layouts
remain in development. The in-memory reference store proves runner mechanics,
not persistence. Abrupt-exit validation is not proof of mid-write atomicity or
power-loss durability; see the validation limitations below.

## Layout

| Path | Project | Purpose |
| --- | --- | --- |
| `contracts/` | Urd.TranscriptHarness.Contracts | Frozen adapter contract (`ITranscriptStore`, `IStoreStatistics`, record/policy types). |
| `core/` | Urd.TranscriptHarness.Core | Deterministic trace generator, oracle model + verification, trace executor, JSON codec, IPC protocol + host server, child-process adapter, stats, path helpers. |
| `reference/` | Urd.TranscriptHarness.ReferenceStore | In-memory reference adapter (correctness baseline; `AckTier.MemoryOnly`, never durability evidence). |
| `host/` | Urd.TranscriptHarness.StoreHost | Store-host child process. Loads the default built-in reference store or any adapter by explicit `--store-assembly` + `--store-type` (AssemblyDependencyResolver, see "Plugin loading"); adding an adapter never edits shared host files. |
| `runner/` | Urd.TranscriptHarness.Runner | Shared runner CLI: `selftest`, `smoke`, `validate`. Owns traces, verification and measurement. |
| `testplugin/` | Urd.TranscriptHarness.Plugin.TestPlugin | Harness-owned resolver-test adapter (Newtonsoft.Json snapshot store, MemoryOnly). Not a storage candidate; never storage evidence. |

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
- **Range read bounds are strict:** `ReadRange`/window reads require
  `0 <= from <= to <= count` for a known session; a violation is an
  `InvalidOperationException` (adapter error), not a silent clamp. Unknown
  sessions return empty. `PageOlder(before, n)` clamps `before` to the count
  FIRST, then subtracts (`to = clamp(before, 0, count); from = max(0, to - n)`),
  so `before > count` yields the tail window, not an empty one.
- **Deletes final-closure** under the assignment invariant: `RequireSettled`
  must fail while any assignment targeting the session is open;
  `CancelOpen` settles them as part of the same logical operation. Dangling
  references to deleted sessions are correctness failures, never quirks.
- **Streaming separates generation chunks from persistence batches:** every
  `StreamDraft` call is one incremental store write batch for an active draft
  stream; a different `streamId` while one is active is an error; nothing
  partial is ever served. `FinalizeStream` marks completion and inserts the
  final message. (Explicitly so stores cannot defer all writes to finalize and
  win streaming comparisons by buffering.) Note: the logical oracle cannot
  observe WHEN a batch's content was written — batch write policy is enforced
  through adapter timing/behavior interpretation and adapter-declared policy,
  not logical verification. Draft-stream identity is per-session unique; until a
  general cross-session stream test exists, the trace generator only uses
  globally unique stream IDs, so collisions across sessions are untested and
  stores must not rely on global stream-ID uniqueness beyond their own state
  partitioning.
- **`FetchToolResult`** is the targeted single-result full-content read path
  (hover/inspection); range reads may still return full content as ordinary
  message payload, and UI list views should read through the preview window
  below.
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
(ready/ack/read/close), host-method timing/allocation recording, plugin load
via AssemblyDependencyResolver with an external dependency, validate-runner
mechanics, kill mechanics, and a regression asserting the kill check's
expected-after model keeps assignment verdicts (a reopened store that lost or
altered its settled assignment fails). All pass against the reference adapter
and the resolver-test plugin.

**Kill tests against memory-only stores validate harness mechanics only.** The
content-based crash-acceptance path exists in code (`Oracle.VerifyAfterCrash`,
and the runner's `validate` command) and activates its verdicts with the first
real persistent adapter; MemoryOnly adapters get structural-coherence-only
runs and explicit verdict suppression.

## Fixture tiers and provenance

- Current milestone: **smoke scale by default** (few sessions, kilobyte-scale text)
  plus the opt-in **Stage1 tier** (below). No huge fixtures are generated, and
  no real session content is read.
- **Stage1 tier (`smoke --tier stage1`)**: two named sessions with UTF-8
  CONTENT byte budgets — `session-large-20m` (~20 MiB) and `session-large-1m`
  (~1 MiB), matching the local census median/p90 — plus smoke-scale small
  sessions in one store. Every Stage1 tool result is bounded to ≤ 256 KiB
  UTF-8 bytes (`Stage1ResultCapBytes`, configurable for downsized self-tests).
  The tier then runs 8 repeated common-workload cycles over the large session:
  append (16 KiB-bounded result) + recent/preview/page/range reads + targeted
  fetch, with a bounded TAIL-ONLY suffix edit every second cycle (reads always
  precede edits; the built fixture is never erased before large reads, and the
  tail edit never reaches into the built budget region). Deterministic default
  seed 4242 (`--seed` still overrides); no benchmark runs automatically.
- **Recorded byte totals are measured actuals, not guessed characters:**
  generator output carries per-Stage1-session build-phase UTF-8 content bytes
  (the budget actuals, recorded in the report notes and trace stats) plus a
  final end-state total across all sessions. Budgets are met within one
  bounded message (~≤ 4 KiB overshoot at most; the final undershoot is a few
  bytes); the actual figure is always the recorded one. End-state differs from
  build totals because cycle appends/edits are workload, not budget.
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
- **Fixture compressibility (no conclusions without measurements):**
  `TextOfrng` draws uniformly from a small synthetic alphabet (letters,
  digits, whitespace, a few multi-byte symbols). That distribution is
  synthetic — NOT English-like and NOT a compression probe; it supports no
  compressibility claim and no compression extrapolation or conclusion of any
  kind without direct measurements. Alphabet symbols include multi-byte
  UTF-8 characters, so recorded byte figures are UTF-8 counts, not string
  lengths. No adapter implements compression, so cross-candidate footprint
  comparisons stay internally fair.

## Measurement rules

- Latency: named statistics only (min/p10/median/p90/max + sample count) —
  no invented percentiles, no aggregate score.
- **Host method vs RPC separation:** every successful store-host response
  carries `t` (store-method elapsed µs) and, when the adapter implements
  `IStoreStatistics`, `a` (managed allocation delta). Both are measured strictly
  around the store method call — request parsing and response JSON
  serialization are excluded. The report carries parent-side RPC latency and
  host-side `hostLatency`/`hostAllocatedBytes` as separate sections (they
  aggregate all calls including verification, and are labelled accordingly).
- Memory/resources reported separately, never merged: managed allocations
  (`IStoreStatistics` where present), child private bytes and working set
  (sampled), store disk footprint (bytes + file count). Replay resource peaks
  include interleaved oracle checkpoint verification (checkpoints run inside
  `Replay`); the final verification pass is sampled separately. No figure
  claims to exclude oracle work from the whole run.
- Adapter LOC vs harness LOC reported separately, alongside dependency and
  install-footprint cost, once adapters exist (measured, not asserted).
- Stdout summaries are convenience output; the machine-readable evidence is the
  run's `report.json` in the owned temp run directory
  (`%TEMP%/urd-transcript-harness/run-<timestamp>-<id>/`).

## Commands

.NET 10 SDK (verified with 10.0.403). Restore is scoped (official feeds only).

```shell
dotnet build experiments/transcript-harness/Urd.TranscriptHarness.sln -c Release
dotnet run --project experiments/transcript-harness/runner/Urd.TranscriptHarness.Runner -c Release -- selftest
dotnet run --project experiments/transcript-harness/runner/Urd.TranscriptHarness.Runner -c Release -- smoke
dotnet run --project experiments/transcript-harness/runner/Urd.TranscriptHarness.Runner -c Release -- validate --store-assembly <dll> --store-type <FullTypeName>
```

`smoke` accepts overrides: `--seed`, `--sessions`, `--messages-min`,
`--messages-max`, `--medium-kb`, `--medium-count`, `--large-kb`,
`--store-assembly <dll>` + `--store-type <FullTypeName>` for a real adapter,
and `--temp-root <dir>`. Large tiers are configuration-only; nothing huge is
generated by default.

## Plugin loading (adapters)

`validate` and `smoke` load adapters by explicit path + type only:
`--store-assembly <dll> --store-type <FullTypeName>`. The store host uses an
AssemblyDependencyResolver-backed AssemblyLoadContext per adapter:

- package dependencies (managed and native) resolve from the adapter's
  `.deps.json` sidecar;
- **shared Contracts types must unify with the host's own loaded instance** —
  the loader refuses to load a second `Urd.TranscriptHarness.Contracts` copy;
- the context is an explicit-path loader for known adapters, not a plugin
  framework: no discovery, no isolation from the host, no sandboxing.

Adapter csproj requirement (coordination note for adapter owners): set
`<EnableDynamicLoading>true</EnableDynamicLoading>` (or equivalent
`CopyLocalLockFileAssemblies`) so package/native assets and `.deps.json` are
placed beside the adapter DLL. The resolver-test plugin (`testplugin/`, an
in-memory store persisting Newtonsoft.Json snapshots) proves dependency
resolution and shared type identity; the first real adapter (SQLite rows)
validates native-asset resolution.

## Persistence validation (`validate`)

`validate` runs a small deterministic acked-prefix workload over the frozen
contract (no contract changes, no extra store API), then:

1. **clean-reopen**: graceful close, fresh host process over the same store
   root, FULL content verification against the baseline model;
2. **kill-reopen**: abrupt process exit with one unacknowledged append sent
   (not awaited) before the kill; after reopen the session content must equal
   the baseline or baseline-plus-inflight end state — resolved by content
   comparison, never by the child merely exiting (the crash-acceptance path
   from Stage 0, first exercised with the persistent adapters). Honesty
   caveat, recorded in the report notes too: this is content-checked
   recovery after an abrupt exit with one unacknowledged request — it does
   not prove mid-operation or torn-write atomicity, and no fault-injection
   engine is claimed; the check works by comparing real reopened content.

Adapters declaring `AckTier.MemoryOnly` (reference store, test plugin) fall
back to structural-coherence-only checks with explicit suppression of all
persistence verdicts: their runs are mechanics information, never durable
evidence.

## Deferred until real adapters

- Attachment files with multiple large-image references per session.
- Cleanup-action footprints (vacuum/compaction timings).
- Stage 1 samples with a shared default OS-flush ack tier across candidates.
- Full benchmark/concurrency suites and large data tiers (parent-serialized).
