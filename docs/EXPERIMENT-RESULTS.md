# Small-operation experiments — Windows first round

**Status:** executed 2026-11-03 on one active Windows 11 development machine. This is an exploratory comparison, **not** a foundation, UI, storage, packaging or cross-platform selection. The approved [proposal](EXPERIMENT-PROPOSAL.md) and its measured implementation are distinct. The optional child-process check and macOS/Linux executions were **not** run.

## Method and setup

The six full experiments ran **serially**, each in a fresh delegated Pie session fixed to **openai-codex / gpt-6.1-sol, medium reasoning**. Bun and Deno ran afterward in their own serial sessions as *runtime variants reusing the TypeScript program*, not independent implementation efforts. No model fallback or parallel candidate run was used. Each language session had a separately reviewed baseline assignment, then a source-change assignment in the same session. This separation and the parent review gap are visible in the timestamps below. Delegates did not inspect other candidates' source or results.

All toolchains and required libraries were installed, pinned, cached and given minimal offline/cached-only readiness checks **before** the first measured session. This untimed setup took place approximately 02:22–02:44 UTC; it included substantial Windows C++ Build Tools installation for Rust. Toolchain/download time is **not** included in implementation or runtime figures. Existing Node/Python were preserved. Toolchain manifest, command arrays, versions, setup logs and cache disclosures: `C:\Users\OwanLazic\AppData\Local\Temp\urd-basic-ops-20261103-8fd88edb\env.json` and `environment\README.md` there. The exact model setting was applied and confirmed before each assignment.

Shared deterministic JSONL fixtures: **1,000 records / 989,274 bytes** and **20,000 / 19,848,048 bytes**, with Unicode, nested values and optional fields. Every candidate loaded/parses, queried latest 50 and selected IDs, appended 32 records to a copy and in memory, removed designated IDs, serialized/reloaded survivors, then added a query-only optional `g3` filter and conditional count summary. The shared external checker verified full records/order, append and survivors (928/18,028) as well as queries; source-change checks verified old no-option behavior. All six baseline and change cases passed. No application, database, UI, network provider, or concurrency simulation was implemented.

The same Windows sampler used fresh-process runs and three in-process repetitions, with 200 ms at each of four memory checkpoints per repetition. **Process wall time includes those 800 ms holds**; phase sums below exclude them, but omit ancillary setup/checkpoint/output overhead. Fresh is not cold disk cache; three repeats do not establish steady-state JIT performance. We recorded OS private committed bytes, working set, CPU and exact per-phase timings in the raw reports. Background development work was not stopped; fixed candidate order was not counterbalanced. Representation/index choices also differ and are disclosed in candidate reports. Small apparent differences are ties, not rankings.

## Milestones and implementation effort

All dates below are **2026-11-03 UTC**. Times are event markers, not model-token or human-effort measurements. Assignment precedes each candidate's first logged action, sometimes after reading the brief. Baseline elapsed means the worker's **first logged start → all baseline measurements verified**; change elapsed means **source-edit-start → source-change-complete**, including tests/runs. They exclude parent review gaps and any pre-log reading, so they are **lower bounds** on end-to-end effort. There were no candidate dependency/download pauses or time deductions. Parent-captured assignment IDs/timestamps and persisted Pie session paths are in `study.json` in the artifact root; delegate milestones and command times are in each `timeline.jsonl`. Pie task records also retain assignment/finish evidence.

| Candidate | Baseline assigned | First correct | All baseline verified | Logged baseline elapsed | Change edit start → verified | Logged change elapsed |
| --- | --- | --- | --- | ---: | --- | ---: |
| TypeScript/Node | 02:46:12 | 02:48:16 | 02:48:25 | 109 s | 02:49:47 → 02:50:42 | 55 s |
| Python | 02:52:27 | 02:54:24 | 02:54:34 | 65 s | 02:55:52 → 02:56:48 | 56 s |
| Kotlin/JVM | 02:58:54 | 03:01:04 | 03:01:15 | 114 s | 03:03:09 → 03:05:25 | 136 s |
| Rust/MSVC | 03:07:23 | 03:09:11 | 03:09:20 | 85 s | 03:10:54 → 03:12:22 | 88 s |
| Go | 03:13:18 | 03:15:19 | 03:15:28 | 99 s | 03:17:07 → 03:19:06 | 119 s |
| C#/.NET JIT | 03:20:07 | 03:22:00 | 03:22:10 | 89 s | 03:24:02 → 03:25:31 | 89 s |

Bun assignment 03:27:26 → first correct 03:28:44 → matrix complete 03:29:03. Deno 03:30:40 → first correct 03:32:10 → matrix complete 03:32:30; direct TS compatibility check finished 03:33:12. These are **reuse/compatibility** tasks, not comparable source-edit efforts. All efforts were well below the proposed caps, but this tiny workload cannot predict long-term maintenance. Minor orchestration retries are documented in individual reports; Deno initially needed `--unstable-detect-cjs` for the unchanged CommonJS compiled JS. Direct TS also passed cached-only Deno checking/run.

## Measured observations (illustrative, not an overall score)

| Candidate | First local build/check, warm dependencies | No-change build/check | 20k fresh operation-phase sum | 20k fresh process wall | 20k fresh private MiB: baseline → parsed → released |
| --- | ---: | ---: | ---: | ---: | --- |
| TypeScript/Node 24.16 / TS 7.0.2 | 0.52 s | 0.46 s | 283 ms | 1,205 ms | 19.9 → 106.1 → 308.6 |
| Python 3.12.14 | 0.18 s syntax check; interpreted | 0.10 s syntax check | 497 ms | 1,477 ms | 10.1 → 59.8 → 17.1 |
| Kotlin 2.4.21 / JDK 21 | 9.70 s | 6.11 s | 642 ms | 1,743 ms | 346.1 → 386.0 → 665.5 |
| Rust 1.99.1 / serde_json | 8.85 s | 0.08 s | 206 ms | 1,097 ms | 1.5 → 64.6 → 3.5 |
| Go 1.27.3 | 5.26 s | 0.18 s | 377 ms | 1,228 ms | 13.1 → 84.3 → 181.3 |
| .NET 10.0.403 / System.Text.Json | 1.45 s offline restore + 3.02 s build | 0.85 s build | 639 ms | 1,635 ms | 8.5 → 125.4 → 182.9 |

Changed-source rebuilds: TypeScript **0.50 s**, Kotlin **7.64 s**, Rust **1.55 s**, Go **0.93 s**, .NET **1.12 s**; Python syntax check **0.25 s**. These processes and scripts were tiny, with cached compiler dependencies. Native two-check execution/compilation varied; see per-candidate reports for exact command timings. Compiled artifacts do not represent installed runtime size. In particular, rising *released* private memory in Node/Go/JVM/.NET after a fixed 200 ms hold is **allocator/GC retention, not a leak finding**; no GC was forced. Kotlin's baseline committed address space is not its resident working set. The first release run, later repetitions and CPU readings are all preserved rather than selecting this one table as a winner.

Bun 1.4.4 and Deno 2.9.8 both verified the unchanged compiled TypeScript implementation across both fixture sizes, fresh/three repeats and no-option/`g3` runs (**16 workloads each**). Their large fresh baseline phase sums were **123 ms** and **250 ms**; walls **1,110 ms** and **1,202 ms**, respectively. Bun loaded the compiled module directly; Deno required the CJS detection flag. This is a narrow Node-compatibility observation, not evidence that Bun/Deno can host Urd or shorten maintenance.

## Interpretation and limits

- All six stacks could express and locally modify this tiny data operation. The variations in build, runtime and memory are worth deeper, representative checks, but no language, runtime or storage engine is selected here. The source programs choose different but ordinary full-record representations and queries; differences are not controlled language-only effects.
- Agent implementation elapsed includes scaffolding, occasional command-wrapper fixes, tests, model response latency and documentation. Same model/reasoning reduces one source of variation but does **not** make elapsed times a language productivity metric. A single serial order and first-exposure effects compound this.
- The fixture is far smaller/simpler than a multi-session agent; no latency under fan-out, background process lifecycle, cancellation, credential route, browser/desktop/VS Code UI sharing, durability, or source-install path was evaluated. Windows console execution does not establish macOS/Linux or desktop portability.
- Optional child-process check was intentionally deferred. Cross-OS rows remain **unexecuted**, not failed. Assess packaging, source-build, credentials, process control and desktop hosts on each intended OS when machines and approval are available.

**Raw reproducible artifacts:** `C:\Users\OwanLazic\AppData\Local\Temp\urd-basic-ops-20261103-8fd88edb\` contains `shared\`, `fixtures\`, `environment\`, and one directory per candidate; reports `BASELINE-RESULT.md`, `CHANGE-RESULT.md`, `VARIANT-RESULT.md`, `timeline.jsonl`, command scripts and `results\*\measurement.json` reside there. This is OS-temp storage, **not a durable archive**; this document retains the important observations and provenance. Do not treat the example source as a Urd implementation.
