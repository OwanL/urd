# Local pruning replay — 2026-11-21

**Decision:** keep the current file-pruning defaults. Test compact skill catalogs further, with independent relevance labels. Do not enable instruction previews or assume that the saved selector answer was correct.

## Where latency went

Read-only historical data from 52 distinct preparations across 48 sessions had usable inputs for all three categories (14 initial primary tasks, four post-compaction primary tasks, 34 subagent tasks). This is workload evidence, not 52 independent catalogs or a before/after test of the staged selector. Timings include exploratory requests. The median from first selector start to last selector finish was **11.18 s**; the median from first prepass activity to the first main request was **11.82 s**. Skills finished last 31 times. Holding tools and instructions fixed, eliminating skill selection altogether would save a median **1.00 s** across all 52, not the entire 9.63 s median duration of skill selection. This ignores the cost of carrying extra skills into later requests. Every skill catalog had at least 230 entries; this sample cannot evaluate bypass for truly small catalogs.

## Skills: fewer tokens is not a quality result

Replayed three distinct saved tasks locally against reconstructed flat and grouped catalogs. The grouped representation shares paths and repeated metadata while retaining per-file IDs. Both arms had the same descriptions available, no skill-body tools, temperature zero, alternating order and a warm local `qwen3.5:9b`. All six requests fit the verified context window. Input tokens totaled **55,401 flat vs 42,660 grouped**; median selector wall time was **17.27 vs 11.92 s**. Kept sets matched between arms on only **one of three** tasks. Neither arm exactly matched the historical answer on any task. This does not identify which answer was better or establish a user-visible latency or billing reduction on Luna.

## Instructions: inspect required rules, not just exact matches

On six real tasks (two reconstructed catalogs), compare the 2 KiB per-item / 16 KiB total inline policy against spending leftover bytes on up to three headings per omitted large document. Using the same local model and no reads, previews changed two kept sets, increasing total selections from 6 to 13. Median selector wall time was **4.21 vs 4.47 s**. Historical agreement is not ground truth; these samples did not exhaust the inline budget or test displaced content.

A separate two-task check allowed bounded, batched reads of recorded candidates in memory. Before scoring, workspace scope, actual shell use and the root document's applicable working rules were checked independently of the kept sets. The original Luna selector **dropped that unprotected root document on one task and retained it on the other**. The local current-style replay missed it on both. The headings arm retained it on one and produced no selection on the other because a requested batch exceeded the experiment limit. That is an inconclusive arm, not a successful prune or a production failure. These are rule-retention observations, not evidence that a downstream task failed.

## Next comparison

Score task-applicable required guidance, helpful optional guidance, and out-of-scope copies separately. Include the scope traps above plus fresh tasks; do not automatically pin every nested instruction file. Compare grouped vs flat skills with the same bounded reads available, and measure both whole-preparation wall time and retained context on follow-ups. Use actual plan-route timings only with approved synthetic inputs. No real prompts or file contents were sent to external providers in these replays; the running host and defaults were unchanged.
