# Transcript parser qualification — bounded synthetic probe

**2026-11-08.** This qualifies a narrow parsing/extraction path, not a production
transcript or dependency choice. [Transcript design](TRANSCRIPT-DESIGN.md) owns the
requirements and approved backend-structure/browser-rendering direction.

## Finding

Markdig can supply the tested structure and small detached middle slices without
rendering a whole message to HTML. **Whole-message parsing is not cheap enough to
repeat indiscriminately during large streamed replies.** Content shape matters:
a 2 MiB table was substantially more expensive than a 2 MiB code fence.

Keep the responsibility split. Reuse completed derivations, coalesce pending work,
release parser trees and bound concurrent parsing. These are supported design
conclusions, not proof that a particular scheduler meets interactive latency targets.
No custom incremental parser or production package selection follows.

## Method and scope

- Windows; .NET SDK **10.0.403**, runtime **10.0.14**; Release console fixture with
  **Markdig 1.6.0**, exact NuGet pin. Pipeline: `UsePipeTables()` and
  `UsePreciseSourceLocation()`. Permissive pipe-table dialect, **not strict GFM**.
- Package SHA-256:
  `5a41668881b286aa531ad37b5c9d4c88bf68da5df1cec9f128f4fce08e510bb2`.
- Generated one large fenced code block, one large two-column plain-cell table,
  many short formatted paragraphs, and a mixed message containing those sections.
  Mixed messages also have a reference link whose definition comes at the end.
  All include accents/emoji. These are sample workloads, not product limits.
- Target sizes **128, 512 and 2048 KiB**; actual source bytes slightly exceed targets
  because generators finish a complete line/row/paragraph. Three measured parses
  per case, after small-input warmup. Fresh process per case, not per iteration.
- Separately grew the same roughly 2 MiB mixed message in **8 or 64** steps, parsing
  the whole prefix each time. Four fresh-process runs in order 8, 64, 64, 8; boundaries
  rounded to complete lines. This tests repeated full parsing, not an incremental
  parser or a timed live stream.
- Parse wall time, process CPU and process-wide allocated bytes exclude source
  generation/copying, projection, validation and serialization. Natural collection
  is included. CPU sampling has coarse resolution; runtime/JIT work and other host
  activity affect results. This is not a statistically converged microbenchmark.
- Live managed-memory snapshots use forced collections outside timed regions.
  Baseline includes the source and warmed runtime state; source and AST stay alive
  through the next snapshot. Their difference is **extra live managed memory**, not
  exact exclusive AST size. Allocated bytes are cumulative churn, not peak memory.
- Process peak working set includes warmup, generation, validation, serialization
  and earlier iterations. It is neither browser memory nor isolated parser memory.

## Results

All correctness checks passed: **36 sample records, four growth records and one
standalone correctness record** across 17 successful fixture processes. Build had
zero warnings/errors. No test child hit its time/memory kill guard.

The following ranges include all three measured parses. Allocation and extra live
memory columns are medians of those three samples, in MiB (1,048,576 bytes).

| Shape | Target KiB | Parse wall ms, min–max | Allocated MiB | Extra live MiB |
| --- | ---: | ---: | ---: | ---: |
| Code | 128 | 2.9–5.3 | 0.69 | 0.35 |
| Mixed | 128 | 14.5–38.4 | 3.25 | 1.72 |
| Paragraphs | 128 | 18.0–19.7 | 2.41 | 1.32 |
| Table | 128 | 16.2–57.2 | 6.41 | 3.55 |
| Code | 512 | 7.6–12.4 | 2.75 | 1.38 |
| Mixed | 512 | 71.1–125.1 | 13.37 | 6.86 |
| Paragraphs | 512 | 64.8–78.7 | 10.06 | 5.30 |
| Table | 512 | 99.7–130.8 | 25.65 | 14.19 |
| Code | 2048 | 27.3–33.3 | 11.00 | 5.50 |
| Mixed | 2048 | 189.1–349.9 | 53.87 | 27.43 |
| Paragraphs | 2048 | 148.4–354.7 | 40.54 | 21.14 |
| Table | 2048 | 342.9–513.6 | 102.59 | 56.75 |

The 2 MiB table consumed **359–578 ms process CPU** per parse. Its largest process
peak working set was **143.73 MiB**, including the non-parser work described above.
Small projection output does not make its initial parse small.

### Repeated full parsing while source grows

Final mixed-message source was **2,097,353 UTF-8 bytes** in every run.

| Prefix parses | Cumulative input parsed | Sum of parse wall time | Sum of process CPU | Cumulative allocation |
| --- | ---: | ---: | ---: | ---: |
| 8, run 1 | 9,557,487 bytes | 713 ms | 891 ms | 231,020,976 bytes |
| 64, run 1 | 69,133,101 bytes | 4093 ms | 4297 ms | 1,616,119,216 bytes |
| 64, run 2 | 69,133,101 bytes | 4118 ms | 4172 ms | 1,605,629,616 bytes |
| 8, run 2 | 9,557,487 bytes | 693 ms | 828 ms | 231,547,368 bytes |

More frequent whole-prefix parsing produced about **7× allocation churn** and
roughly **5.7–5.9× summed parse wall time** here. This does not choose an update
frequency: perceived streaming delay, actual token rates and scheduling were not
measured. Peak process working sets for these runs were approximately 114–139 MiB;
the 1.6 billion allocated bytes were **not simultaneously resident memory**.

### Extraction and correctness

- Detached projections contain up to three middle code lines plus language, three
  middle table rows plus headers/alignment, and three paragraph source ranges with
  short previews. For 2 MiB inputs their serialized size was **283–932 bytes**.
  Extraction wall time ranged **0.02–5.10 ms** across those cases.
- Extraction traverses an already parsed document; generic paragraph selection
  scans/materializes paragraph references. Output size is bounded in this fixture,
  but traversal/allocation is **not a qualified indexed bounded-range read**.
- Code/row/range content, indices and counts matched generator formulas, rather
  than merely matching a parser-renderer round trip. Fixed fence/table/list checks
  passed. A distant reference resolved on the full source and remained unresolved
  before its definition arrived. Emoji/accent source spans mapped correctly from
  inclusive UTF-16 offsets to UTF-8 byte ranges in the tested example.
- A no-inline probe let its document weak reference clear after collection while
  detached projection content survived. This establishes that document's
  collectability, **not** complete source-string/pool release or library leak freedom.

## Limits and next engineering consequence

No SQLite range index, production API, browser renderer, scrolling, selection,
syntax highlighting, image decode, multi-session contention or live provider was
included. Tables had simple cells; paragraphs were separate, short blocks. Giant
single inline constructs, long single lines, nested structures and rich table-cell
slicing remain unqualified. No Markdown-wide correctness or memory ceiling follows.

The probe did **not** implement coalescing, cancellation, projection persistence or
incremental parsing. It supports avoiding per-token/full-prefix reparsing, not a
claim that merely throttling it solves large-message streaming. A non-incremental
parse already running can still consume its full cost; cancelling a queued request
is not proof that an in-progress parser stopped.

Next design work should make the parse workload explicit: demand-driven reusable
structure for saved content; bounded shared scheduling; consistent source extents;
and an honest policy for a growing message whose formatting work falls behind.
Do not turn the measured sizes/guards into product limits or build a custom parser
without evidence that simpler scheduling/reuse cannot satisfy the workload.

## Evidence and reproduction

Owned fixture: `%TEMP%/urd-transcript-parser-20261108-184b27/`.
It retains `Program.cs`, project/pins/config, `run.ps1`, `README.md`, source hashes,
`summary.json`, package-local caches and `runs/` JSONL/metadata/build logs. Temporary
artifacts are not production source or a permanent performance test suite.

Acquire/start exclusive `urd:heavy-local-work` before restore/build/runs. `run.ps1`
sets fixture-local NuGet/CLI caches and runs one child at a time: `build`, then
`run correctness`, `run sample <kind> <KiB>`, or `run growth <steps> <KiB>`.
The run guard waits up to 60 seconds for 900 MiB free host memory, uses a 384 MiB
managed-heap hard limit, and stops its own child after 60 seconds or sampled private
memory above 512 MiB. These are experiment safeguards, not Urd budgets. Initial
preflight refusals started no child; later runs retained the same memory threshold.
No OS/runtime installation or machine-wide configuration changed.

The run held a started exclusive resource lease. Owned dotnet processes were gone
before release. No live application, provider account or `C:/dev/data/urd` access
occurred. Independent static method review preceded execution. A separate final
review recomputed the summaries from raw records, checked the fixture and guards,
and approved the claims/limits with no actionable findings. It did not rerun the
fixture or independently inspect the lease; resource ownership/cleanup above is
reported from the parent session's tool evidence.

## Bounded Stage 2 follow-up — 2026-11-10

This isolated decision probe extends, but does not supersede, the released-parser
measurements above. It used pinned Markdig 1.6.0, markdown-it 14.1.0, Roslyn
CSharp 4.14.0 and highlight.js 11.11.1, temporary fixtures and synthetic inputs.
Images were literal/deferred. **Browser canonical DOM fidelity matched 8/11 cases.**
The exact three differences were: nested-list newline/text-selection equivalence;
permissive pipe-table handling of an unescaped pipe in code versus markdown-it row
splitting; and rejected JavaScript-link Markdown remaining literal in markdown-it
versus backend label semantics without an executable href (block-HTML wrapping also
differed in that safety case). Both tested paths emitted no script/image elements,
event attributes or JavaScript hrefs, but this narrow corpus is not a sanitizer or
conformance qualification. Resolve dialect and fallback/fidelity policy before
integration; do not call this packet 1 sign-off.

The fixture confirmed useful transfer reduction: middle payloads were **988–2,008
bytes** versus full projections of **2.14–3.76 MB**, though initial derivation was
still required and the helper scanned/materialized projections rather than providing
an indexed range read. The first 1 KiB serialization was 56.4 ms; warmed common-case
parse+projection+serialization component sums were **0.79–1.29 ms**. Browser
projection decoding was 0–0.5 ms, DOM construction 0.2–0.7 ms, and mount plus two
animation frames 26–30 ms. These are component observations, not commit-to-paint or
end-to-end latency. Diagnostic browser batches produced 60–98 ms Markdown long
tasks; large/highlight diagnostics also exceeded the 50 ms main-thread target.

Highlighting is context-stateful: isolated-window highlight.js lost an opening
comment and all 3,800 tested visible UTF-16 units had scopes differing from full
context. Full-context browser and backend lexical ranges agreed in that window;
category taxonomies differ elsewhere. Prefer demand-driven context-aware derivation
and transfer only needed ranges; reject naive stateless arbitrary-window highlighting.
No production highlighter selected. Same-process reuse improved a synthetic repeated
request sequence, supporting bounded process-local caching, but fixed ordering and
warmup confounded totals. There is **no persistent-cache justification** from these
restart/revisit observations; no persistence or package selection follows.

In a 512 KiB replay, the latest parse completed **103 ms after final source arrival**,
before projection/display; this does not pass the ≤100 ms visibility gate or measure
actual visibility. Single-worker synthetic contention showed increased wake lateness,
not Urd storage/execution lock contention. Release probes showed tracked references
clearing and bulk browser DOM release, but not baseline memory recovery or a memory
ceiling. In-flight synchronous parsing was not cancelled by replacing stale queued
work.

Remaining gates include semantic/dialect and unsafe-markup fallback policy; broader
constructs and source/revision/ancestor-aware indexed slices; scheduled parse through
actual visibility under execution-write contention and memory pressure; stateful,
off-thread highlighting; huge inline/table-cell cases; cache eviction during
selection; browser baseline recovery; and composed Stage 2 targets. No live provider,
production integration, virtualized UI, actual browser paint timing or production
package evaluation was performed. Raw report and full fixture: `%TEMP%/urd-stage2-decisions-20261110-4f84ef2a/REPORT.md` and neighboring records.
