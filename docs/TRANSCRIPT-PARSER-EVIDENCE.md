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
Images were literal/deferred. Its initial **8/11 browser canonical DOM fidelity**
result and provisional differences below are superseded by the targeted fidelity
follow-up at the end of this document.
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

The table dialect decision is now standard GFM: unescaped pipes inside table code
spans delimit cells, while escaped pipes remain content. The permissive Markdig
behavior measured by this probe is not the selected dialect, and this GFM behavior
has not been implemented or qualified in a production parser. The unsafe-link/raw-HTML
handling and fallback policy remains open, as does list-selection fidelity.

Remaining gates include broader constructs and source/revision/ancestor-aware indexed
slices; scheduled parse through actual visibility under execution-write contention
and memory pressure; stateful, off-thread highlighting; huge inline/table-cell cases;
cache eviction during selection; browser baseline recovery; and composed Stage 2
targets. No live provider,
production integration, virtualized UI, actual browser paint timing or production
package evaluation was performed. Raw report and full fixture: `%TEMP%/urd-stage2-decisions-20261110-4f84ef2a/REPORT.md` and neighboring records.

## Targeted fidelity follow-up — 2026-11-10

The isolated follow-up report is
`%TEMP%/urd-stage2-fidelity-20261110-250eb606/FOLLOWUP-REPORT.md` (reproduction
instructions and hashes are alongside it). It refines the earlier fidelity probes;
it does **not** qualify packet 1, a production parser, or sanitizer/security behavior.

- **GFM table cases: 6/6 passed.** Markdig 1.6.0's strict
  `PipeTableOptions.UseGfmRules=true` option was documented, compiled and exercised
  in the fixture. Cases cover unescaped pipes inside code spans splitting cells,
  escaped pipes remaining content, plain escaped pipes, padding/truncation,
  alignment, and invalid/empty delimiter rows. This is a narrow set of explicit
  expectations, not general GFM conformance or production-parser qualification.
- **Browser: 132/132 assertions passed** over 24 fixtures and five selection cases.
  Native Chromium pointer drag and Shift+click each passed (2/2), preserving selected
  text and native selection endpoints after a visible append-only update. Retention
  was demonstrated only when unchanged existing DOM/text nodes were retained and new
  children appended. Full remount lost selection. A pure-keyboard exploratory
  attempt in a non-editable focused element created no selection and is not a
  keyboard qualification.
- **Unsafe Markdown labels/raw HTML:** tested rejected destinations rendered label
  children in inert `span` elements without anchor/href/link role/tab stop.
  JavaScript variants, data, VBScript and protocol-relative destinations were
  exercised; safe links and escaped cases remained correct. Raw inline/block HTML,
  comments, iframe/srcdoc and SVG were rendered as literal text; pre-wrapped spans
  preserved exact source, visible text and native selected text including newlines.
  No source-created executable/resource elements, event attributes or execution
  sentinel activation occurred. This corpus is **not sanitizer sign-off** or
  comprehensive hostile-URL testing.

Remaining limits are exact: keyboard-created selection; arbitrary
updates/remounts or insertions affecting selected content; clipboard behavior and
other browsers; broader Markdown/GFM and URL/security coverage; and composed
streaming/display/highlighting qualification (including actual visibility latency,
contention, cancellation/staleness/failure, resource bounds/recovery, and
stateful/off-thread highlighting). Images remain deferred. The report does not sign
off packet 1, sanitizer behavior, production activation, or composed Stage 2 targets.

This evidence now supports an **initial Markdig semantic-derivation
implementation only**, subject to those gates; it does not choose a production
highlighter, persistent-cache mechanism, or package choices for highlighting/cache.
The selected GFM behavior is qualified only in the six fixture cases, not in a
production parser.

## Current production-display builder probe — 2026-11-10

This bounded local probe compiled the unchanged current production builder, source
map, index, range reader, cache and DTO implementations into an isolated Release
fixture. All 12 cases completed without guard trips; no production service, database,
app or provider was started. Each fresh process warmed on 1 KiB, then measured one
synchronous whole-source Build, including parsing and detached index construction.
These are builder timings, not parse-only timings. Build allocation is current-thread
churn; accounted index bytes are the implementation's retention formula, not CLR
heap size. Peak working set includes runtime, source and transient work.

| Case | Source bytes | Build ms | Build allocated bytes | Index accounted bytes | Peak working set bytes |
| --- | ---: | ---: | ---: | ---: | ---: |
| Common | 870 | 0.56 | 90,264 | 16,998 | 40,755,200 |
| Table | 524,282 | 465.9 | 159,126,984 | 23,100,100 | 137,715,712 |
| Code | 2,097,105 | 129.5 | 60,086,808 | 16,852,416 | 74,682,368 |
| Mixed | 2,097,010 | 1,549.6 | 324,132,448 | 55,365,948 | 238,186,496 |

Retention is strongly shape-dependent: for ~512 KiB, accounted index bytes ranged
from ~1.12 MB for a giant line to 4.21 MB for code, 13.84 MB for mixed Markdown and
23.10 MB for a table. Thus no numeric cache defaults are selected. A per-entry
retention estimate alone also misses the potentially substantial cost of building
one accepted source. Numeric entries/bytes/pending-work defaults need a coherent
server working-set, scheduler/concurrency and deployment-headroom basis.

Middle-page results were bounded (about 16 KiB); warmed read-plus-serialize medians
were about 0.22–0.26 ms for the large code, mixed and table cases (30 samples each).
Actual cache Add/Find/Get reuse, one-entry eviction, stale-handle rejection, session
invalidation, Clear and post-scope full-GC index weak-reference release all passed.
These are single-entry/release checks, not multi-message cache stability or proof of
memory recovery.

The source report and retained raw records are in
[`%TEMP%/urd-display-production-f50fc475-835d-469c-92b6-a552fa6cea93/REPORT.md`](file:///C:/Users/OwanLazic/AppData/Local/Temp/urd-display-production-f50fc475-835d-469c-92b6-a552fa6cea93/REPORT.md)
and that directory's `all-metrics.json`, `process-results.json`, per-case JSON/Markdown
and production snapshots. This evidence supports bounded production-builder index
reads and tested cache release only. It is not end-to-end, memory-recovery or
streaming sign-off. Server activation still needs coherent multi-message working-set,
scheduler/contention and browser evidence.

## Composed production parser/cache working-set probe — 2026-11-10

This final synthetic, in-process probe compiled unchanged production backend sources
at exact HEAD `e4f5ecc97bd954d23e45a176244df89617bf6fa3` and exercised the real
`MarkdownParser`, scheduler, cache, leases, page reader, source map and DTOs. It
passed **1,943 assertions**; all four owned child processes exited 0. No repository
source was modified for the probe. The deterministic capture delegate is synthetic:
there was no database, storage/session capture path, application service, UI, browser,
live session or provider. This is bounded fixture evidence, not E2E or a production
benchmark.

### Composed working set and parser cost

| Source | Actual UTF-8 bytes | Parse + index ms | Managed allocated bytes |
| --- | ---: | ---: | ---: |
| Common cold message | 870 | 72.5913 | 151,736 |
| Table | 524,282 | 572.6632 | 159,159,768 |
| Code | 2,097,105 | 181.2655 | 60,090,352 |
| Mixed, old generation | 2,097,010 | 1,709.4371 | 324,255,664 |
| Mixed, appended generation | 2,097,053 | 1,467.3978 | 323,370,080 |
| Mixed contention specimen | 2,097,010 | 1,085.3008 | 323,365,592 |

The largest named composed handoff retained **64 entries / 151,748,836 accounted
bytes**, with both old and appended mixed generations pinned; accounted bytes are the
cache's conservative retention formula, not CLR heap or transient parser memory.
The composed process OS peak working set was **426,971,136 bytes** (sampled maximum
private bytes 384,081,920). The short common miss arrived during an actual large
synchronous parse: its request-to-lease wait was **1,066.4388 ms**, while its own
parse took **0.2616 ms**. This demonstrates head-of-line waiting in this fixture,
not a service/UI latency or a production target. Marker timing includes a small
pre-call harness offset and scheduling/publication.

All 60 repeated common requests hit cached generations. Under both entry and byte
pressure, admission of the appended generation failed with the expected
`MarkdownCacheFullException` while the old pinned pages remained readable and
accounting unchanged; releasing the old lease allowed eviction/retry, and generation
checks distinguished rebuild from a warm hit. The queue case verified supersession,
duplicate joining, unrelated-key `MarkdownQueueFullException`, delivery/release of
independent leases, and zero final accounting. These correctness cases do not imply
that retention limits cap parse work or transient memory: each pressure process
allocated about **1.688 billion managed bytes** across five whole mixed parses,
including rejected publications.

After composed release/drain/disposal, entries, accounted bytes, external pins and
interests were zero. Without forced GC, estimated managed memory remained about
189.9 MB through 15 seconds, then was about 0.99 MB at 30 seconds and 1.02 MB at
60 seconds; working set fell from 268.1 MB at 0 seconds to 54.4 MB at 30 seconds.
A natural collection occurred between 15 and 30 seconds in this run, not on a
guaranteed deadline. At final measurement all tracked source/index weak references
were dead. Pressure/queue immediate snapshots with zero accounting do not establish
immediate collection; some weak references remained alive before collection.
Explicit-GC diagnostic measurements are not a production recovery proposal.

### Streaming and qualification limits

A separate read-only Markdig 1.6.0 feasibility inspection found public low-level
line-continuation support, but no safe resumable document/checkpoint API. Backward
invalidation and potentially unbounded parse tails prevent treating that primitive
as safe incremental document parsing; whole-source parsing remains the current
approach. This is a feasibility finding, not approval of a fast lane or a change to
design invariants.

The append fixture added only **43 bytes** to an identical committed prefix. It does
not qualify repeated streaming growth, bounded memory/latency, or arbitrary edits.
The probe does not set production defaults or recommend its generous probe limits.
No DB/UI/E2E, production-default or streaming-growth qualification follows. Timing and
memory depend on fixture shape, runtime, diagnostics, GC and host scheduling; managed
allocation is cumulative churn, not live memory.

### Evidence and reproduction

The full report and raw records are temporary at
`C:/Users/OwanLazic/AppData/Local/Temp/urd-markdown-composed-f16e8e19-07e7-4c1c-8728-add556b521c6/REPORT.md`
(and neighboring files). This path is a local reproduction artifact, not durable
evidence storage.

## Real-parser two-lane follow-up — 2026-11-10

The temporary report is `%TEMP%/urd-two-lane-9e4c421d-0fc8-4b4e-bf5b-1804de189961/REPORT.md` (with raw records and reproduction artifacts alongside it). The probe compiled an unchanged production snapshot at HEAD `acca084eb8899e2a4a5ab079798a78bfd91a6ab7`; all 79 copied production inputs matched HEAD blobs and before/after hashes. It used the real production scheduler, cache, Markdig parser/index builder and page reader, with a synthetic capture delegate.

After 3 warmups, all 25 measured synthetic request-to-lease short requests completed below 100 ms: p50 **0.4479 ms**, p95 **0.7947 ms**, max **0.8279 ms**. Short own-parse p50 was **0.2341 ms**, max **0.5838 ms**. The large parse took about **1.22 s** (p50 1,221.0931 ms; max 1,338.8267 ms) and allocated about **323 MB cumulatively** (max 323,369,368 bytes). Runtime Markdig-allocation evidence verified each short arrival occurred while the large parse was active; arrivals were near parse start, not randomized across parse progress. One large and one small BelowNormal production scheduler thread handled the work, with no same-lane overlap. All **1,208 assertions** passed.

The probe used a **384 MiB hard GC heap cap** and a sampled **500 MiB private-byte guard**; neither tripped. OS peak working set was **273,088,512 bytes**. The probe-only limits were Entries=1, RetainedBytes=64 MiB, PendingKeys=2, Leases=4 and SmallMessageUtf8Bytes=65,536; these are not production defaults. Accounting returned to zero after each iteration (entries, retained bytes, pending keys, external pins and interests), so large indices did not accumulate in retained cache ownership. This is not per-iteration accounting of all process memory.

After release/drain/disposal, natural memory recovery had **not** occurred by 30 seconds: no later gen2 collection was observed, and one tracked index and one source weak reference were still alive at the final unforced-GC check. Zero cache accounting is not immediate CLR reclamation. This does not establish a leak or that all objects remain alive.

This is synthetic request-to-lease evidence, **not DOM/E2E or commit-to-visible latency**. It is not a production SLA, confidence interval, randomized contention study, production-default recommendation, or proof that cache byte bounds cap transient/process memory. Temporary artifacts are not durable evidence storage.

## Standalone virtualized browser and generation-handoff evidence — 2026-11-11

The standalone browser pipeline traversed **512 units forward and backward**. Peak
rendering was 49 code pieces with 51 contexts and 200 table pieces with 252 contexts;
maximum source-anchor error was 0.422 px for code and 0 px for tables. There were no
long tasks above 50 ms, and visual review approved the result. The composed generation
handoff probe measured 0 px anchor error and no long tasks; native selection/focus
pins deferred catch-up as intended. This is standalone synthetic evidence, not a live
Transcript/routes activation. Only complete code lines and table rows are bounded;
arbitrary giant paragraphs, lines, cells and metadata are not. Selection can defer
catch-up, and unmount loses selection.

## Synthetic cache-candidate qualification at HEAD 7f461c8

The exact candidate sequence was: **8/8/128 MiB failed before handoff** because of
slots; **12/12/128 MiB achieved 0/5 handoffs**, below the required 138,335,594 bytes;
**12/12/160 MiB passed 5/5**, with target 155,660,064 bytes, observed projection
155,663,760 bytes and 12,108,400 bytes headroom. The code cache entry was evicted at
first handoff; a third mixed projection of 195,490,320 bytes was rejected with readable
old/new generations. Small cache misses completed in 0.48–2.99 ms. Sampled private bytes
were 417,193,984 and working set 448,159,744. Weak references were dead by 30 seconds and
cache accounting was zero.

This candidate is qualified only for synthetic composition and is **not selected as a
production default**. A cache bound is not a process/transient-memory bound, and natural
recovery is not guaranteed by a deadline. The exact temporary reports/evidence are in:

- `C:/Users/OwanLazic/AppData/Local/Temp/urd-markdown-renderer-GbOjhQ/`
- `C:/Users/OwanLazic/AppData/Local/Temp/urd-generation-handoff-qualification-20261111-042836-7972/`
- `C:/Users/OwanLazic/AppData/Local/Temp/urd-exact-candidate-23d2a970-5fd6-4787-a6a5-3be67b72aa99/`
- `C:/Users/OwanLazic/AppData/Local/Temp/urd-exact-candidate-12x12-56975e8e-8602-4028-813f-550005c09e87/`
- `C:/Users/OwanLazic/AppData/Local/Temp/urd-exact-candidate-12x12-160mib-01641b5b-b008-40b2-bbe5-b00a647aca3e/`

## Fixture HTTP/browser composition at HEAD 1697571 — 2026-11-11

A temporary fixture host connected real stored Markdown capture, the scheduler, Markdig
and page reader through Kestrel JSON to the standalone browser renderer. It did not map
production routes or modify `Transcript.tsx`. Code and table fixtures each traversed 512
units in both directions. Peak piece/context/DOM counts were 208/72/284 for code and
212/569/794 for tables; maximum piece response bodies were 6,382 and 5,478 bytes.
Initial open and assembly took 407.90 ms for code and 1,119.40 ms for tables. Publish to
the next animation frame peaked at 5.20 and 8.50 ms respectively, with no observed long
tasks. These are fixture timings, not a 100 ms end-to-end small-message result: serial
context HTTP reads dominated, including 1,387 code and 10,304 table requests.

Ten changed-source publications retained the old generation until replacement, suppressed
a stale candidate and awaited release. Thirteen opens had thirteen releases. Consumer
release left the expected twelve-entry warm cache; explicit disposal returned leases,
entries, retained bytes and pending keys to zero. Chromium heap fell between the 5- and
15-second unforced samples, but neither recovery deadlines nor leak freedom are claimed.
Native Ctrl+C replaced a distinct clipboard sentinel with the selected source text.

Actual tab hiding and suspension remain unqualified: focusing a second headless page did
not hide the document. CDP accepted frozen/active commands, but no freeze/resume events
occurred and the page timer continued. The runner therefore reports `blocked`, not full
acceptance. No live history invalidation, provider streaming, concurrent parse stress or
production cache defaults are established. The report, raw evidence and reproduction are
at `C:/Users/OwanLazic/AppData/Local/Temp/urd-composed-1697571-o3wCsY/`; earlier flawed
attempts and the pre-sentinel clipboard run are preserved as non-qualifying attempts.

### Bounded descriptor-batch follow-up

The same frozen baseline plus five captured batch-source files reduced descriptor-related
HTTP requests from **1,387 to 148** for code and **10,304 to 486** for tables. Single-run
initial open/assembly changed from 407.9 to **374 ms**, and 1,119.4 to **184.5 ms**,
respectively. A 40-ID batch paged as 32 + 8 descriptors within 16 KiB; variable metadata
remained separately paged. Both fixtures retained their 512-unit forward/backward traversal,
selection/clipboard, trim/rehydrate, ten handoffs and stale-open cleanup checks.

This is not the full current checkout: later public Core facade/serializer changes were
excluded. One **65 ms** table long task was observed. Actual hidden visibility and
freeze/resume remained unqualified (46 timer ticks during the freeze command), so lifecycle
acceptance is still blocked. No production latency, memory budget or activation follows.
The owned processes stopped, leases were released and the original evidence was unchanged.
Report, input hashes, measured results and cleanup proof:
`C:/Users/OwanLazic/AppData/Local/Temp/urd-batched-context-qual-qZWACy/`.

## Actual page-owned HTTP/client fixture — 2026-11-11

Frozen production Kestrel routes plus the real browser client/assembler/renderer now
pass native-format, bounded-response, failure-cleanup and SSE orphan-reclamation probes.
The production browser bundle was served directly by Kestrel, without a development
proxy. The source capture is labelled `7b28297`; backend binaries were copied from the
previous isolated build, not rebuilt from that capture. Exact input/loaded-binary hashes
are recorded. Later source-anchor and background-tool changes are outside this probe.

For an 870-byte message, 25 warmed-runtime **cache misses** produced distinct generations
and measured median **49.6 ms**, p95 **54.0 ms**, maximum **54.6 ms**. Another 25 cache-hit
reacquire/assembly trials measured median **27.0 ms**, p95 **58.5 ms**, maximum **58.6 ms**.
First-ever process/browser formatting was separately **329.1–446.2 ms** across two launches.
These intervals include HTTP open, complete fixture assembly, DOM commit and two animation
frames; they exclude SSE setup/release and are not actual retained-tab activation timings.
Complete 512-line code / 512-row table fixtures took **295.9 / 689.9 ms**, with **41 / 199**
HTTP calls including open/release. Observed pages stayed within 16 KiB / 256 records.

Missing/stale opens, the candidate 12-lease cap, disconnect reclamation, fresh-page reopen
and awaited release passed. Shutdown ended with zero entries, retained bytes and pending
keys; this is logical cleanup, **not heap/resident-memory recovery evidence**. Warm-tab
latency, first-ever formatting, real transcript composition, long-task/frame behavior,
contention, memory and actual hidden/freeze lifecycle gates remain unqualified. No
production limits or activation follow. All owned processes/ports stopped; the user's
running server remained untouched. Full report, raw trials, hashes and cleanup proof:
`C:/Users/OwanLazic/AppData/Local/Temp/urd-real-http-qual-ob68YR/REPORT.md`.
