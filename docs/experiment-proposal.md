# Small-operation technology experiments — revised proposal

Status: approved and executed for the Windows first round on 2026-11-03; see
[observations and limits](experiment-results.md). This replaces the earlier
session/UI/model-loop proposal, which was too application-shaped and too narrow
in its initial candidates. Two bounded design passes informed this revision.
The planned macOS/Linux executions and optional process check remain unexecuted.

## Execution controls agreed with the owner

- One fresh delegated session per language, running the complete exercises in
  sequence. Run these sessions serially, not in parallel. Additional TS runtime
  checks also run serially.
- Use the same model and reasoning for every experiment: GPT-6.1 Sol, medium
  reasoning. Resolve and verify the provider/model identity before assignment;
  do not silently substitute another model or use automatic fallback.
- Prepare and pin every candidate's toolchain and dependencies before the first
  experiment. Pre-fetch packages and verify a minimal build/check can run without
  downloads. Use the same small readiness check, not a prebuilt solution to the
  measured tasks. Environment setup is an untimed prerequisite, not a scored task.
- Exclude installation, downloads and dependency-repair waits from comparative
  implementation/build/runtime metrics. Pause and log unexpected dependency gaps
  rather than charging network/setup delays to a candidate. Keep interruption
  timestamps separately so wall-clock and adjusted timings remain auditable.
- Record assignment/start, correct baseline, execution/check completion, source
  change verified and finish milestones. Distinguish model/implementation elapsed
  time, local build/check time and program execution time; retain raw timestamps.

## Scope: small command-line programs, all six languages

Include **TypeScript/Node, Python, Kotlin/JVM, Rust, Go and C#/.NET JIT in the first
round**. Run the same TS source under **Bun and Deno** too, recording compatibility
changes and runtime-specific preparation. Do not reserve languages for a later
round based on the previous shortlist.

Each gets a small console program using ordinary file/collection/JSON facilities
and, where needed, one normal JSON library. No HTTP framework, model SDK, browser,
agent loop, sessions, database or fake provider. This compares concrete runtime/
library configurations, not every framework in an ecosystem. Pin and disclose
versions and typed-record versus generic-JSON representation choices.

## Core exercises

| Exercise | Equivalent work | Useful observations |
| --- | --- | --- |
| **Read, parse and query** | Read the same UTF-8 JSONL file; parse its complete records; select the latest 50 and find specified IDs. | Fresh-process cost, read/parse/query time, CPU and memory with data loaded. |
| **Append, remove and save** | Append a fixed batch in memory and to a fresh copy of the input file; remove a specified set of IDs in memory; serialize survivors to a new file and reload it. Preserve ordering and all surviving fields. | Collection/mutation costs, serialization and ordinary file I/O; memory before/after releasing references. |
| **Make a source change** | Add an optional group filter and summary to the query command, preserving its old behaviour when the option is absent; add two small checks, rebuild/check and rerun. | Files/code changed, dependencies, edit/test/build steps and friction. This is a change to the experiment, not a Urd feature implementation. |

Use only two deterministic fixture sizes initially: roughly **1 MiB and 20 MiB**
(e.g. 1,000 and 20,000 records near 1 KiB each; record actual counts/bytes). Include
text with some Unicode, IDs, groups, nested metadata and a few optional fields.
These are convenient test inputs, not Urd workload or capacity commitments.

Separate the append operation from the removal rewrite: JSONL append and whole-file
rewrite are different workloads. A successful write/reload is not a crash-durability
claim. Fresh copies prevent runs accumulating data. All mutation stays inside the
experiment's owned temporary directory; no application/user data is used.

Match logical results, not byte-identical JSON whitespace/key order. One short
shared generator/checker verifies counts, selected IDs, order and preserved data
against expected output. Timed results count only after correctness is checked.
Keep read, parse, mutation and write observations separate where easy, and also
report total elapsed time; do not build instrumentation merely to separate them.
No custom parser, regex substitute, database/index design or hand-tuned data layout.

## One optional non-data check

If the core exercises remain small, run the **same harmless child command** from
each program, read its output and terminate it after a readiness signal. This
samples a relevant OS/I/O boundary without building Urd's tool system. Record
child-exit behaviour and cancellation delay, separating helper cost from parent
cost. Use one shared helper, not a child written in each candidate language whose
startup would confound the comparison. No process-tree manager, concurrency
framework or desktop automation. Defer this check rather than letting it expand.

## Lightweight measurement and maintenance notes

- Use a small shared fixture/check script, ordinary build/run commands and existing
  OS process tools. No benchmark framework, dashboard, orchestration service or UI.
- Record a fresh-process run and three in-process repetitions using fresh input
  state; label them separately. Following the agreed serial-session workflow, use
  a disclosed fixed language order and measure on the same machine. This is not
  counterbalanced ordering; retain order/background-load caveats. Fresh process
  is not cold disk cache. Preserve raw readings and
  ranges; use no percentile claims or artificial overall score. A few repetitions
  do not establish steady-state JIT performance.
- Observe process memory at baseline, with parsed data, after mutation and after
  references are released with a short fixed wait. Use the same OS metrics within
  an OS (e.g. private committed memory and working set on Windows). Do not compare
  language-specific heap counters as total RAM or force GC in the baseline.
  Allocator/GC retention after the wait neither proves a leak nor predicts Urd.
- Separate program execution from compilation and downloads. Record prerequisites,
  first build with dependencies available, no-change repeat and rebuild/check after
  the source edit. For interpreted modes, record the actual check/run workflow;
  do not invent a build step. Use ordinary release settings, not dev servers or
  bespoke optimisations. Artifact size without required runtimes is not install size.
- Note owned code, checks, generated files and dependencies separately. Effort and
  retries are rough anecdotes affected by agent familiarity, not language scores.
  Review equivalent work before attributing a striking difference to technology.

## Windows → macOS → Linux

Keep **every candidate** in the platform matrix. Execute on Windows first, then
macOS and Linux when machines/toolchains are available. Repeat the same small
build/run/source-change exercise, not a port of an application. Label each result
**executed**, **documented only** or **blocked/unavailable**. A cross-compiled binary
is not execution evidence. Record exact OS/toolchain environments; do not generalise
one Linux environment to all distributions or desktop/display systems.

No machine installations, remote infrastructure provisioning or paid services are
implicitly authorised. Missing access/toolchains are reported, not used to silently
drop a language. Desktop hosting, OS permissions, credentials and full integration
portability remain separate later questions; console success cannot qualify them.

## Scope limits and output

Suggested stop-and-report caps, not promised durations: **30 minutes for shared
scaffolding; 60 minutes per language including the source change; 15 minutes for
each additional TS runtime attempt**. Record downloads/setup waits separately and
pause blocked work rather than spending hours solving environment issues. Apply
the same small-attempt principle to additional OS runs. A timebox limit is not a
technology failure, and near-ties remain ties.

Return one compact comparison table, simple reproduction commands, raw observations
and caveats. These are heuristics for data handling, runtime overhead, local edits
and basic portability—not UI responsiveness, production storage, agent fan-out,
provider compatibility or long-term maintenance predictions. Results should identify
which tiny follow-up is worth doing, not select Urd's foundation by JSON speed.
Larger whole-product performance qualification remains separate. This first round does
not measure full agent workloads, resource cleanup, source distribution or client latency.
