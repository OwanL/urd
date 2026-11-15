# Pruning classification: accuracy, risk and latency

**2026-11-15 — follow-up experiment, not production qualification.**

## Answer

**Conservative parallel selection (CG) is the strongest low-latency pruning
candidate in this sample. It is not a demonstrated winner over keeping everything
when losing useful capabilities is expensive.**

Across eighteen evaluation tasks, CG retained **55.7% of optionally useful
capabilities**, removed **96.0% of irrelevant capabilities**, and took **2.59 seconds
on average**. It preserved every labelled required capability group. Its remaining
44.3% useful-capability loss is substantial, not a success hidden by high accuracy.

At a **5:1** penalty for pruning something useful versus retaining something
irrelevant, keep-all had the lowest observed average loss. Differences from the
strongest pruning candidates were uncertain. At **10:1 and 20:1**, keep-all was
clearly better in the observed loss estimates. At **2:1**, pruning was preferable.
These are explicit policy trade-offs, not measured downstream task-failure costs.

This evidence does not reverse the owner's decision to retain AI tool/skill
pruning. It does rule out calling any tested design a proven low-risk winner.

## What was run

**480 additional preparations**, beyond the [first 306](pruning-replay-results.md):

| Block | Tasks × repeats | Strategies | Preparations |
| --- | --- | --- | ---: |
| Fixed-guidance pilot | 6 × 1 | BU, BG, CU, CG, CX, ALL | 36 |
| Fixed-guidance comparison | 12 × 3 | same six | 216 |
| Fixed-guidance replication | 6 × 3 | same six | 108 |
| Per-item scoring pilot | 6 × 1 | QU, QG | 12 |
| Per-item scoring comparison | 12 × 3 | QU, QG | 72 |
| Per-item scoring replication | 6 × 3 | QU, QG | 36 |

The complete classification export covers **786 preparations, ten result blocks,
43 strategy/block combinations**, with raw/final and domain breakdowns. Recorded
requests across those blocks total **1,820**, including the original bounded main
continuations; nine separate preflight/recovery requests are outside that total.
Reviewer calls are not counted as experimental route calls.

Exact route remained `openai-codex/gpt-6-luna`, low reasoning, provider-default
sampling, no retries. Tasks, candidate inventories and task-only labels were unchanged.
The new prompts and penalty ratios were frozen before their respective comparison
calls. Pilots tested mechanics; no case-specific prompt or label tuning followed.
Per-item scoring was an explicitly separate adaptive follow-up motivated by poor
optional-useful recall, not a retroactive change to the first comparison.

The eighteen evaluation tasks are twelve original comparison cases plus six
previously inspected confirmation cases. This is **replication on a known corpus,
not a fresh blind holdout**. Three repeats measure variation; they do not turn
18 tasks into 54 independent tasks. Pilots and historical protocols are not pooled
into the headline averages.

## Definitions: useful loss matters more than ordinary accuracy

Positive means **prune**:

- **False positive (FP):** a useful capability was pruned — the costly error.
- **False negative (FN):** an irrelevant capability was retained.
- **True negative (TN):** a useful capability was retained.
- **True positive (TP):** an irrelevant capability was pruned.

Optional usefulness is judged against the sealed task-visible rubric, not proved
by subsequent successful task execution. Protected capabilities are excluded from
classifier accuracy. Uncertain labels are excluded and counted. Required `anyOf`
groups are scored separately: keeping one valid alternative satisfies the group;
we do not falsely require every substitute. Cross-domain groups use global
satisfaction and their domain denominators can overlap.

Useful retention is `TN/(TN+FP)`; irrelevant removal is `TP/(TP+FN)`.
Accuracy is `(TP+TN)/(TP+TN+FP+FN)`. F2 gives retention recall more influence
than precision. The CSV also supplies balanced accuracy.

Weighted loss is:

`(penalty × useful_pruned + irrelevant_retained) / (penalty × useful_total + irrelevant_total)`

We report penalties **2, 5, 10 and 20**, with **5** the declared primary setting,
not an owner-approved valuation. Lower loss is better. Headline losses average
each task equally after pooling its repeats; confusion counts and recall pool
labelled candidate decisions. Required critical misses remain a separate gate.

## Eighteen-task averages

All follow-up strategies retain resolved instructions deterministically, in
resolver order. Every selector receives the same full guidance. Grouped variants
make two parallel calls, one for tools and one for the whole skill catalog; these
are **not per-skill calls**.

| Strategy | Useful retained | Irrelevant removed | Accuracy | Mean preparation | p95 | Context retained |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| BU: original combined selector | 38.7% | 97.6% | 79.2% | 2.94 s | 7.53 s | 30.4% |
| BG: original parallel selectors | 47.4% | 96.4% | 81.0% | 3.79 s | 6.46 s | 31.6% |
| CU: conservative combined | 52.5% | 97.7% | **83.6%** | 2.84 s | 3.98 s | 31.7% |
| CG: conservative parallel | 55.7% | 96.0% | 83.4% | **2.59 s** | **3.68 s** | 32.3% |
| CX: conservative combined + inspection | 47.8% | 95.6% | 80.6% | 5.93 s | 7.54 s | 31.6% |
| QU: combined per-item score, keep ≥17/100 | **59.4%** | 86.3% | 77.9% | 7.10 s | 9.92 s | 42.8% |
| QG: parallel per-item scores, keep ≥17/100 | 57.2% | 93.4% | 82.1% | 5.20 s | 8.21 s | 40.5% |
| ALL: keep everything | 100% | 0% | 31.3% | <0.01 s | <0.01 s | 100% |

Each row has 54 attempts. BG has one censored 60-second preparation: included in
latency, excluded from classification/context, not treated as an empty successful
selection. The other rows have 54 scored preparations. QU has five full-selection
fallbacks; QG has two domain fallbacks. Their extra retention is partly fallback,
not classifier skill. The other follow-up variants had no fallback.

Context is serialized prepared characters relative to matched keep-all inputs,
not measured provider-token savings. Timing begins after frozen input loading;
it is **preparation latency, not end-to-end startup or task completion**. Score
versus selection-list timing comes from different batches and is descriptive;
parallel-versus-combined timing within each batch is paired.

### Full confusion counts and weighted loss

There are normally 318 optional-useful and 699 irrelevant decisions per row.
BG's censored run reduces these to 312 and 685. Columns are intentionally named
rather than relying on ambiguous positive/negative terminology.

| Strategy | Useful pruned FP | Useful kept TN | Irrelevant kept FN | Irrelevant pruned TP | Loss 2 | Loss 5 | Loss 10 | Loss 20 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| BU | 195 | 123 | 17 | 682 | .307 | .436 | .510 | .558 |
| BG | 164 | 148 | 25 | 660 | .269 | .376 | .437 | .477 |
| CU | 151 | 167 | 16 | 683 | .237 | .335 | .392 | .429 |
| CG | 141 | 177 | 28 | 671 | **.232** | .320 | .371 | .404 |
| CX | 166 | 152 | 31 | 668 | .271 | .376 | .435 | .474 |
| QU | 129 | 189 | 96 | 603 | .263 | .322 | .356 | .377 |
| QG | 136 | 182 | 46 | 653 | .236 | .315 | .360 | .389 |
| ALL | 0 | 318 | 699 | 0 | .523 | **.305** | **.180** | **.099** |

All scored follow-up evaluation runs retained the required groups: **0/225 missed**
per complete arm, BG **0/221**. Instruction retention is mechanical, not model
accuracy credit. This does not erase losses of useful-but-not-mandatory capabilities.

### Replication and uncertainty

| Strategy | Useful retention: 12-task block | Useful retention: 6-task block |
| --- | ---: | ---: |
| CU | 56.5% | 45.0% |
| CG | 58.5% | 50.5% |
| CX | 43.5% | 55.9% |
| QU | 62.8% | 53.2% |
| QG | 56.0% | 59.5% |

The reversal for inspection shows why one aggregate is not universal evidence.
Case-cluster bootstrap intervals resample tasks, not individual repeated decisions:

- CG minus CU: **−0.015** loss at weight 5, 95% interval **[−0.038, +0.009]**.
  CG is the better point estimate, not a proven quality winner.
- CG minus CU preparation: **−0.258 s**, interval **[−0.489, −0.057]**. A modest
  measured preparation difference, not a two-second startup improvement.
- CG minus ALL: **+0.015** loss at weight 5, interval **[−0.033, +0.065]**.
  No clear superiority either way at that penalty.

Intervals describe this small purposive corpus and are not multiplicity-adjusted
claims over a broad strategy search. The all-case BG average is survivor-conditional;
strict complete-pair rankings remain available separately in private analysis.

Skills remain the weak point: CG retained **65.7% of useful tools but only 34.3%
of useful skills** across the eighteen tasks. Required skills survived; optional
workflow help was frequently lost. Broad within-skill context alone did not solve it.

## Confidence thresholds did not solve harmful pruning

QU/QG ask for a usefulness estimate for **every** candidate, explicitly including
optional help and useful substitutes. Retention uses `p ≥ 1/(penalty+1)`, frozen
before comparison, rather than choosing a threshold after seeing labels.
These are **uncalibrated model estimates**, not reliable probabilities.

For example, in the comparison block, the combined model's 0–20% bin averaged
**1.23% predicted usefulness**, but **16.1% were labelled useful**. The parallel
model's corresponding observed rate was **17.7%**. Scores were strongly
underconfident about optional usefulness relative to this rubric.

| Combined score threshold | Useful retention, comparison | Useful retention, replication |
| --- | ---: | ---: |
| ≥34/100, penalty 2 | 52.4% | 37.4% |
| ≥17/100, penalty 5 | 59.3% | 47.5% |
| ≥10/100, penalty 10 | 68.8% | 57.6% |
| ≥5/100, penalty 20 | 75.7% | 65.7% |

Threshold rows reuse the same responses, **not additional independent runs**.
They exclude invalid score outputs: 3/36 comparison and 2/18 replication runs for
QU; QG excludes 2/36 and 0/18. The actual-outcome tables above include fail-open
fallback instead. At penalties 10 and 20, both threshold-adjusted strategies still
had higher paired loss than keep-all in both blocks; observed bootstrap intervals
excluded zero, conditional on complete valid-score cases. Higher retention did
not compensate for the greater cost assigned to the remaining misses.

## Historical runs and complete export

The earlier comparison selected guidance too; it is not directly interchangeable
with the fixed-guidance experiments. Its classification records, both pilots, and
the keep-all main-continuation block are included **separately** in
[the full CSV](pruning-classification-summary.csv).

The CSV contains **344 rows**: 43 strategy/block combinations × two stages × four
scopes (all/tools/skills/guidance). It supplies confusion counts, optional recall,
accuracy, balanced accuracy, F2, required-group misses, uncertainty/protection
exclusions, mean/median/p95 latency and all four case-macro losses. Raw guidance
is excluded for fixed-guidance selectors; final guidance retention is still audited.
Scopes/stages overlap: **do not sum their run counts**. CSV `confirmation` split
names are inherited dataset identifiers; follow-up replication is not blind holdout.

## Evidence limits and verification

These results measure selection against predeclared expert judgments on bounded
contemporary catalogs and pre-boundary file snapshots. They do not measure real
recovery cost, eventual instruction compliance, successful edits or total task time.
Model confidence cannot convert those judgments into task-success probabilities.
No production code, class structure, installed build or user data was changed.

Local deterministic tests cover selection/fallback/cancellation, guidance order,
score envelopes, threshold boundaries, classification formulas and paired grouping.
Independent reviews checked both experiment adapters and analysis; a supported SDK
thinking-envelope issue was corrected before scored provider calls. Hash checks
verified every persisted request, original prompt, new result manifest and fixed
guidance order. Owned runners have stopped; the provider reservation was released.

Private artifacts remain in `%TEMP%\urd-pruning-replay-20261115-133437`, including
`experiment-v6`, `experiment-score` and `classification`. The private evidence index
SHA-256 is `a97d5328ef28078e0718e5ffb5160e7973f3756f9ac80b1596f30e984c71daf1`.
Temporary traces are not durable backup; only sanitized results are committed.
