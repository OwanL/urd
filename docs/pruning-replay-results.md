# Pruning replay results

**2026-11-15 — controlled research, not production qualification.**

## Outcome

**Follow-up:** [classification, asymmetric error costs and latency](pruning-classification-results.md)
now cover these runs plus fixed-guidance and per-item-scoring experiments. This
page retains the original protocol's findings; it is not the latest comparison.

**None of the tested pruning bundles qualifies for recommendation.** They removed
69–73% of the prepared context, but each omitted required whole-file guidance in
7–11 of its 36 comparison runs. These are rubric failures, not demonstrated failures
of the original executable tasks. No pruning startup/readiness advantage was established.

This does **not** reverse the [decision to retain AI tool/skill pruning](ARCHITECTURE.md#keep-ai-based-tool-and-skill-pruning-with-protected-core-capabilities).
It prevents choosing a production preparation bundle from these results. Instruction
retention needs separate treatment before a fresh tool/skill-selection comparison.

The completed protocol is preserved in Git at
`0a368a2:docs/pruning-replay-test-plan.md`. No application implementation, provider
configuration, active installation or user data was changed by this study.

## Inputs and controls

- **24 tasks from 24 distinct source sessions:** six pilot, twelve comparison and
  six confirmation cases, selected before variant results. They cover Pie, Urd,
  Reveal and a general question. Sampling was purposive, not representative prevalence.
- **Contemporary-catalog replays, not historical reproductions.** Historical prompts
  were paired with explicit pre-boundary Git trees and a frozen current catalog.
  The original historical branch/system prompt is not established. No later commits
  or working-tree answers were copied into the fixtures.
- **Complete within the declared local scope:** 21 installed Pie/pinned SDK tool
  definitions, nine resolved bundled/user skills, and resolved instruction layers.
  MCP/web integrations, executable user extensions and project skills were excluded;
  this is not a complete historical or active-session Pie inventory.
- Core `read`, `bash`, `edit`, `write`, `ask_user`, `request_capability` and
  `invoke_tool` were protected outside selection. No guidance was protected using
  the answer key. Whole guidance bodies were supplied; skill bodies loaded on use.
- Task-only labels received independent review and were sealed before variant calls.
  Later transcript usage was not treated as necessity. Guidance precedence was a
  declared contemporary policy, enforced after selection rather than decided by
  the model. Every scored guidance file contained an applicable obligation under
  the conservative whole-file rubric; optional-guidance discrimination is untested.
- Inspection used immutable in-memory file maps: no host filesystem, shell, edits,
  delegation or historical side effects. Each preparation allowed two tool-result
  cycles per selector, twelve inspections, 96 KiB returned evidence and a 60-second
  watchdog. Caches started empty; concurrent domain batches had stable admission order.

The initial uncertified extraction produced no scored cases and was excluded. Its
historical scan-limit certification was incomplete; the scored bundles and their
input hashes were rebuilt and checked separately.

## Model and comparison

Exact route: **`openai-codex/gpt-6-luna`**, installed
`openai-codex-responses@0.80.6` adapter, low reasoning, provider-default sampling,
no temperature/top-p/seed supplied, no transport retries. Returned SDK metadata
matched this route. This establishes the callable route, not independent attestation
of the provider's underlying deployment. Three preflight calls checked structured
output and inspection. Twenty deterministic harness checks passed.

Twelve comparison cases × three repeats × six arms produced **216 preparations**
and **841 attempted selection calls**. Preparations ran serially; G0/G1 domain calls
ran concurrently. Paired arms shared candidate order, varied across repeats, with
counterbalanced arm order. Repeats are not independent tasks.

| Bundle | Critical-miss runs /36 | Affected cases /12 | Median preparation | Observed p95 / max | Context retained |
| --- | ---: | ---: | ---: | ---: | ---: |
| U0: combined, no exploration | 7 + 1 timeout | 5 | 3.28 s | 12.49 / 60.02 s | 31.2% |
| G0: parallel whole domains | 10 | 8 | 5.53 s | 10.10 / 10.82 s | 31.0% |
| U1: combined, bounded exploration | 11 | 7 | 6.21 s | 8.34 / 10.88 s | 27.1% |
| G1: parallel domains, exploration | 10 | 7 | 6.60 s | 8.27 / 8.84 s | 28.6% |
| S1: G1 + conditional shared excerpts* | 11 | 8 | 6.86 s | 9.00 / 16.16 s | 31.0% |
| Keep-all | 0 | 0 | 0.001 s | 0.002 / 0.002 s | 100% |

Preparation medians are medians of twelve case-level medians; p95/max describe the
36 observed runs, not an SLA. Context is the mean paired fraction of serialized
prepared bundles, including schemas, guidance and wrapper, **not exact provider
prompt tokens**. The timed stage starts with frozen inputs loaded; it excludes
shared cold SDK/input bootstrap and is **not user-facing startup latency**. The
canceled U0 run is not scored as a completed selection or a small usable context.

*The S1 explicit-markdown/plan trigger activated **zero times** on the comparison
block. This row is an additional G1 sample, not evidence about effective shared
grounding. A pilot explicit-plan case benefited from eager evidence in U0 once,
but shared evidence did not consistently repair its review-path omission. That is
exploratory evidence only; shared/ambiguous grounding remains unresolved.*

Relative to U0, median paired case-level preparation differences were +2.06 s for
G0 and +2.92 s for U1. G0 was faster in four of twelve cases; U1 in one. These are
preparation observations, not proof of startup improvement or statistical superiority.
The frozen meaningful-latency criterion was both 2 seconds and 20% on qualified
same-run readiness comparisons; no pruner reached that qualification stage.

### Quality, fallback and context

All surviving critical omissions were guidance: **9 / 10 / 14 / 13 / 12 document
omissions** for U0/G0/U1/G1/S1 respectively. Global working-directory rules and
append-system working guidance were lost despite their full contents being visible.
No protected capability was lost. Required skill coverage survived final closure,
but some raw-failed selections were rescued by fallback, not accurate pruning.
G1 also omitted the study-labelled direct delegation tool in two of three required
opportunities; those were noncritical, recoverable-horizon misses.

U0 had one full-catalog fallback and one joined watchdog cancellation; G1 had three
failed-domain fallbacks and S1 four. G0/U1 had none. Raw failures, selected IDs and
final closure remain separate in the private traces. Cancellation never became fallback.

Irrelevant prepared payload fell sharply: approximate mean per-run character/4
estimates were 418 / 23 / 178 / 361 / 517 for U0/G0/U1/G1/S1, versus 9,890 for
keep-all. These are estimates, not provider token attribution. Useful candidate
retention also fell; uncertain labels were excluded from hard quality denominators.
Thus reduced context is not automatically all waste removed.

Exploration issued 63 inspections for U1 and 192 for G1, with 9 versus 30 logical
file reads; other inspections included candidate/list/search operations. Recorded
truncations were 2 and 8. Evidence availability and duplication differ, so U1/G1
are complete preparation-bundle comparisons, not a pure grouping experiment.

### Individual comparison cases

Each cell sequence is **U0 / G0 / U1 / G1 / S1**. Misses count runs out of three;
`T` is the separate censored U0 preparation. Keep-all had zero misses throughout.

| Case | Critical-miss runs | Median preparation seconds |
| --- | --- | --- |
| e01 | 2 / 1 / 0 / 2 / 2 | 2.69 / 7.00 / 5.84 / 6.36 / 7.35 |
| e02 | 0 / 1 / 0 / 0 / 0 | 3.54 / 5.57 / 6.36 / 6.74 / 7.35 |
| e03 | 0+T / 0 / 0 / 0 / 0 | 9.12 / 6.69 / 6.13 / 6.72 / 6.68 |
| e04 | 0 / 1 / 0 / 1 / 2 | 4.46 / 3.58 / 5.93 / 7.87 / 6.54 |
| e05 | 0 / 0 / 1 / 0 / 0 | 2.86 / 2.76 / 4.89 / 6.15 / 6.78 |
| e06 | 0 / 0 / 1 / 0 / 0 | 3.33 / 5.49 / 4.47 / 5.98 / 6.94 |
| e07 | 1 / 1 / 2 / 2 / 1 | 2.64 / 6.24 / 5.94 / 6.63 / 7.43 |
| e08 | 0 / 1 / 0 / 1 / 1 | 3.45 / 6.75 / 6.46 / 7.29 / 7.26 |
| e09 | 0 / 2 / 1 / 1 / 1 | 3.17 / 9.54 / 6.42 / 6.51 / 6.63 |
| e10 | 1 / 0 / 2 / 2 / 2 | 3.23 / 5.32 / 6.28 / 6.57 / 6.34 |
| e11 | 2 / 1 / 2 / 1 / 1 | 3.22 / 5.06 / 6.63 / 6.26 / 6.68 |
| e12 | 1 / 2 / 2 / 0 / 1 | 5.96 / 2.76 / 7.45 / 7.21 / 8.25 |

## Confirmation and recovery

The predeclared gate admitted **no pruning finalist**. All six untouched cases
therefore received keep-all only, three repeats each, with preparation immediately
followed by its own main continuation. This preserves the gate rather than forcing
a winner; it does not supply a comparative pruning startup estimate.

The three-response probe made 34 main calls. Twelve runs ended with an answer and
six reached the response cap. Independent, variant/timing-blind review found only
**one of eighteen** reached its predeclared file-grounded readiness milestone
(c04 repeat 3, answer complete at 14.95 seconds from admission). The other cases
had no passing repeat. Many replies proposed future inspection rather than
establishing source evidence. These are observed milestone non-attainments, not
proof of eventual task failure. The weak control probe needs pilot calibration
before it can support a later readiness comparison; it was not retuned on holdout.

First visible text was observed in twelve runs; six had none before their cutoff.
The conditional median was 4.39 seconds, not a whole-block startup estimate.
Common confirmation bootstrap was 0.178 seconds for input loading and 1.613 seconds
for SDK/client setup, excluded from the warm preparation clock. Provider queue
separation and cold-cache control were unavailable.

A separate, explicitly coached recovery calibration deliberately omitted `find`.
Luna listed capabilities, disclosed the exact definition and invoked it through the
initial stable `invoke_tool` wrapper, without changing native tools. The fixture
returned matching files; no effect ran. An initial virtual-glob defect was corrected
and regression-tested before confirmation; both calibration records were retained.
This proves that narrow wrapper path, not spontaneous recovery, native append,
production permissions or full task completion. No general guidance recovery was invented.

## Usage, limits and next experiment

Comparison terminal SDK usage was **2,236,129 input, 39,883 output and 40,064 cache-read
tokens** across 840 non-error/non-aborted calls; the canceled call's usage is unknown.
Separately reported reasoning was 4,216 tokens, already included in output rather
than added again. Local catalog pricing estimated about $0.244, not actual billing.
Confirmation reported 425,549 input, 3,271 output and 118,144 cache-read tokens.
Provider cache state was uncontrolled. Including pilots, both calibration records
and three route preflights, the recorded experimental route attempted 1,113 calls.

Important gaps: the local catalogs had fixed tool/skill counts; real empty catalogs,
assignment roles, project/MCP skills and a nested-scope instruction differential were
not qualified. Deterministic tests covered empty/protected-only skipping and boundary
failures. All claims are restricted to these fixtures, model route and early horizon.
No production session persistence, Stop races, native append, cache savings or task
completion was qualified.

**Next bounded experiment, not an approved implementation:** preserve resolved
applicable instruction files deterministically, compare combined versus grouped
tool/skill selection, and validate a plan-reference trigger on fresh explicit and
ambiguous cases before spending on evaluation. Calibrate the main readiness probe
on pilot inputs first. Keep broad skill context and test recovery separately.
This study does not settle Urd's class structure or loading/protection policy.

## Private evidence

Artifacts remain outside Git under `%TEMP%\urd-pruning-replay-20261115-133437`:
sealed cases/labels/catalog, protocol revisions, all requests/results, blind judgments,
metrics and the reproducible verifier. Integrity checks covered 306 scored/pilot
preparations plus two separate calibration records; no credentials were bundled.
Owned runners stopped and the provider reservation was released.

- Cases SHA-256: `2474d485162fea43acb2fceac8a5a659baf4ebd695875897e3582e75c8c0ca8b`.
- Evidence-index SHA-256: `50ea75d644f82d8de0740ffaa5b7b125969c3f9223f24182781e4c9d05bcbdea`.

The temporary evidence is local and not a durable backup. Only this sanitized report
is committed; private prompts, file snapshots and model traces are not published.
