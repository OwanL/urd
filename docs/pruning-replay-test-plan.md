# Pruning replay test plan

**2026-11-15 — proposed experiment; not implementation or benchmark evidence.**
Owner scope: tool, skill and instruction-document selection, including `AGENTS.md`
and append-system resources, with dynamic recovery. Use real Pie tasks, catalogs,
files and model calls to compare preparation designs and inform later Urd structure.
This study measures selection and early task readiness, not full task completion or
production session correctness.

## Decisions and limits

- Use **GPT 6 Luna** for experimental selection, grounding and bounded continuation.
  This supersedes the earlier suggestion to use the current conversation's model.
  The local catalog identifies `openai-codex/gpt-6-luna`; confirm that exact callable
  route in preflight. Record actual provider/model, endpoint class, adapter revision,
  reasoning and supported sampling settings. Never silently substitute a model.
- **Kimi K3.1 Pro** independently grills this plan. Its configured review route is
  `ollama/kimi-k3.1-pro:cloud`. A localhost Ollama endpoint is not evidence of local
  inference; this is the named cloud model. Review evidence belongs at the end.
- The owner imposes **no spending limit**. Still record usage, keep experiments
  finite, respect provider concurrency, and stop broken or uninformative runs.
  No automatic top-ups, account changes or unrelated model calls are implied.
- This deliverable is the reviewed plan. It does not claim the replay harness,
  datasets or benchmarks have been built/run, or authorize a production rewrite.
  Live experiments are opt-in research, separate from the fake-provider routine
  suite described in [testing](testing.md).
- Retain [architecture](ARCHITECTURE.md#keep-ai-based-tool-and-skill-pruning-with-protected-core-capabilities)
  and [discovery](discovery-design.md) boundaries: protected entries never enter
  selector candidates; restored exposure is not current execution permission;
  preserve prefixes on ordinary continuation; never replay effects.

## Questions the experiment must answer

1. Does one combined selector or three parallel whole-domain selectors give the
   best quality/latency balance? Keep all skills together within their selector;
   the owner reports poorer results from one inference per skill. Do not make that
   rejected design the main experiment again.
2. When does reading task files or candidate bodies justify extra model rounds?
   Can explicit file references be resolved without another inference?
3. Is shared task grounding better than independent exploration by three selectors?
   Measure the added serial barrier, duplicate work and inconsistent interpretations.
4. Can guidance selection remove meaningful context without losing task obligations?
   Does selection need explicit cross-domain dependencies?
5. Does recovery repair omissions quickly enough, or erase startup savings?
6. Which observed preparation/recovery behaviors impose requirements on later
   integration and its cancellation/durability tests? Experimental jobs are not
   production `Session` subclasses.

## 1. Build a small, credible corpus

### Unit of replay and source inventory

A case is **one selection boundary**, normally the first accepted task prompt.
The replay view contains only information available at that boundary. Record the
transcript branch/entry identity and stop the evaluation horizon at the next
material task-changing user instruction. Later context windows/compaction can be
separate cases; never use future steering as a requirement for initial selection.

Start with a bounded inventory of local Pie transcripts and catalog-selection logs.
Read streaming/bounded records, not entire large transcripts into memory. Inspect
source formats before assuming a transcript contains its assembled system prompt.
Do not modify live Pie data, repositories, personal instructions or provider config.

Current feasibility inspection found:

- `C:\dev\repos\pie\data\pruning.jsonl` and rotations contain task queries,
  selected names, response/usage estimates and pruning-code versions; linked session
  headers provide time and working directory. Skill-read/recovery events are useful
  behavioral evidence, not a complete catalog or ground truth.
- These records do **not** reliably preserve the full pre-prune catalog, assembled
  selector/system inputs, all instruction documents or historical file contents.
  A recorded context-file path or pruning-code version is not a file snapshot.
- A transcript's already-pruned visible tool list cannot stand in for the full
  eligible catalog. Doing so hides the original selector's false negatives.

Assign every case an explicit fidelity tier:

| Tier | Meaning and use |
| --- | --- |
| Exact input snapshot | All task-visible inputs and eligible definitions are captured/versioned. Exact input fidelity does not promise deterministic LLM output. |
| Reconstructed historical case | Inputs reconstructed from trace evidence and matching Git objects; list every missing/substituted component. Use supported portions, not invented historical facts. |
| Contemporary task replay | Historical prompt plus explicitly frozen current files/catalog. Useful controlled task, **not** historical reproduction. Re-annotate against these actual inputs. |

Record **catalog-complete: yes/no independently of fidelity tier**, with evidence for
all eligible tools, skills, guidance, protected entries, definitions/versions and source
scope/order. A logged selected subset is never a complete inventory. Hard selection
scores and keep-all comparisons require a complete frozen inventory: exact snapshots
or contemporary cases explicitly re-annotated against that inventory. Incomplete
historical reconstructions are qualitative diagnostics only; they cannot estimate
historical false negatives. Exclude missing required file evidence from hard scoring too.

Do not block the study waiting for unattainable historical fidelity. Start with honest
contemporary cases and add prospective complete snapshots. Historical prompts remain
useful, but report provenance separately; never claim historical correctness from
current catalogs/files.

### Frozen case bundle

Store private bundles and experiment artifacts in an owned OS-temp directory,
not Git or source/documentation trees. Keep their location and hashes in the local
run manifest. Retain them through review; remove only known task-owned artifacts.
Commit only the plan and a sanitized aggregate report. Do not copy credentials.

Each bundle contains:

- Original prompt/visible preceding context, session kind/assignment, working root
  mapping and the exact replay boundary. Preserve attachments if materially needed;
  otherwise exclude the case with a reason instead of silently losing information.
- Full **eligible pre-prune** tool, skill and guidance inventories: stable IDs,
  source/surface identities, descriptions/relevance metadata, schemas, protected
  status, source order/scope and explicit dependencies. Retain full bodies separately.
- Immutable file tree material needed for genuine navigation, including plausible
  distractors. Preserve relative references and map historical absolute paths to
  the fixture root. Record content hashes and Git revision where available. Use
  Git object/archive reads without switching the user's checkout or using worktrees.
- Files created/changed by the subsequent execution must not leak into initial
  task evidence. Do not expose later answers through plans, logs or generated files.
- An evaluator-only bundle containing the subsequent transcript, evidence citations,
  labels, uncertainties and expected observable milestones. It must be inaccessible
  to replay tools and absent from model requests, including hints in filenames.
- Provenance/redaction notes. Screen selected inputs for secrets/unrelated private
  material before transmitting them to the named providers; quarantine unsuitable
  cases rather than changing the task invisibly.

### Sampling and separation

Target **24 independent tasks: 6 development/pilot, 12 frozen comparison, and 6
untouched confirmation cases**. Only catalog-complete cases count toward these totals.
The comparison block selects finalists; the confirmation block checks them on fresh
inputs. Choose all splits/strata before seeing variant results, covering multiple
repositories and:

- Clear self-contained tasks; tiny/no-optional catalogs where selection should skip.
- Explicit plan paths and ambiguous/unresolved plan references.
- Overlapping skills; cross-domain tasks; skill-to-tool/guidance dependencies.
- Scoped/nested instruction documents and append-system guidance whose importance
  is not obvious from filenames; an omission that causes an observable violation.
- Small and large catalogs, irrelevant near-matches, essential text late in files,
  unavailable references, and a task where needless exploration only adds delay.

Select by a recorded stratification rule, not by known successes. Keep related
sessions, plan revisions and continuations in the same split. Confirmation cases
must cover indirect references, guidance obligations and straightforward tasks; do
not choose them after seeing which cases favor a finalist. Record unavailable strata
and exclusions; do not silently reduce to easy cases. Synthetic distractors or
failure cases are clearly separate from the real-corpus score.

## 2. Define evidence-based expectations

The historical agent is not an oracle. Used does not mean uniquely necessary;
unused does not mean irrelevant. Transcript success also does not prove that all
applicable instructions were followed.

For each candidate, label **required**, **useful/acceptable**, **irrelevant**, or
**uncertain**, with a short task/source citation. Record valid substitutes as
alternative sets: do not require both tools when either can accomplish the task.
For required entries, also record whether needed at startup, recoverable later
within the same task, or outside this boundary's horizon. Distinguish discovering
skill metadata from actually loading its body.

Freeze a **task-visible-only annotation before opening the subsequent transcript**.
Historical use/success then becomes a separate diagnostic, never a necessity label.
A proposed label correction must cite task-visible evidence and receive independent
review; historical success/use alone cannot justify it. Keep the original annotation
and revision reason. An independent reviewer audits every critical obligation/required
label and a sample of other labels, blind to variant identity/results. Resolve supported
corrections before sealing; unresolved disagreements remain uncertain and outside
hard-label denominators. Use the owner only for consequential policy ambiguity,
not routine annotation. Report uncertainty and annotation coverage, not fake precision.

Before annotation, define a deterministic **guidance resolver** in the case manifest:
source roots and session role, ancestor/nested scope, source precedence and ordering,
shadowing/replacement versus additive append behavior, protected base, and whole-file
pruning units. Derive recorded Pie behavior from its actual assembly path where known;
otherwise declare a frozen contemporary research policy. Missing or ambiguous policy
excludes a case from hard instruction scoring. Every arm, including keep-all, receives
the same resolved applicable universe; no selector decides precedence. Alternative
resolver policies require separate labelled sensitivity runs, never pooled results.

This is not a new Urd loading policy. Do not automatically protect every `AGENTS.md`/
append document, which would avoid the requested test. A prunable file containing a
mandatory applicable obligation is labelled **required as a whole file**; omitting it
fails that rubric. Do not leak gold labels by automatically promoting those files into
the protected input set. Mixed mandatory/optional files may limit attainable savings;
report that rather than adding fragment rewriting. Protected status comes from the
frozen source policy, not evaluator judgments. Core/pinned tools and skills remain
excluded before selection and enforced afterward.

## 3. Controlled design comparison

### Common contract

All variants receive identical original task facts and candidate metadata, stable
IDs and ordering within a paired repeat. Tool inputs normally use relevance text
rather than full schemas; skills use name/description; guidance needs meaningful
content or authored selection metadata, not just filenames. Freeze this input
representation across variants. Do not create a fresh LLM summary for one arm only.
Count any metadata preparation in end-to-end latency and usage.

Each selector returns kept IDs, explicit unresolved-evidence status and compact
source references where needed; no long chain-of-thought or essay is required.
Validate IDs, eligibility and dependencies deterministically. Record raw selection
and final dependency/protection closure separately so forced additions do not mask
selector defects. Never invent dependency edges from the answer key.

Return empty optional selection when appropriate. Empty domains and all-protected
catalogs skip inference. Oversized inputs or malformed responses are recorded
outcomes; no silent truncation, model switch or retry-until-success.

### Primary arms

| Arm | Grouping | Evidence access |
| --- | --- | --- |
| U0 | One selector sees all three catalogs | Supplied task/metadata only; one inference |
| G0 | Three parallel selectors, one per whole catalog | Same supplied task/metadata only; one inference per nonempty domain |
| U1 | One selector sees all three catalogs | Bounded read/list/search and candidate-body inspection |
| G1 | Three parallel whole-domain selectors | Identical inspection tools and shared preparation-scoped read cache; no mandatory grounding barrier |
| Keep-all control | No selector | All eligible entries, protected base and the same recovery mechanism; records input size and downstream behavior |

U0/G0 isolate grouping without exploration. **U1/G1 compare complete preparation
bundles, not a pure grouping effect:** evidence availability, duplicate retrieval and
shared-budget contention differ. Report starvation/truncation and logical reads/bytes
per branch separately from deduplicated physical I/O. U0/U1 and G0/G1 show the value
and cost of evidence access within those bundles. Add an identical-frozen-evidence
ablation only if causal grouping attribution changes the decision; do not multiply
arms merely to explain every interaction. A tool call and the response using its
result are **two model rounds**, not a one-call selector.
A full-catalog context overflow is a control result, not grounds to silently drop
that case or shorten its catalog.

### Targeted follow-up: shared grounding and eager reads

After pilot results, freeze one shared-grounding challenger alongside the primary
arms for evaluation if indirect tasks justify it. It uses the same G1 selectors,
initial inputs and total evidence allowance; the only change is a bounded grounding
job before parallel selection when a frozen, task-input-only trigger fires. Preserve
the original prompt and cited excerpts; a summary cannot replace the authoritative
instruction text or silently choose between ambiguous plans. Count grounding in
rounds, usage and wall time. Log trigger misses and needless trigger activation.

Separately test deterministic eager reading of explicit file paths on pilot cases,
with and without it on the same grouping. Count its cost and late-file truncation.
Do not conflate this extra evidence with a grouping improvement. Freeze any adopted
policy before evaluation; never cache model-grounding answers between repeats/arms.

### Exploration limits and isolation

Use real read/list/search operations on the frozen fixture tree, not transcript
answers supplied on demand. Expose full candidate bodies only through declared
inspection paths. Selection candidates are **not** the pruner's executable tools.
MCP, edit, write, shell and delegation definitions may be selected without granting
those effects to the experiment. Do not replay historical commands or contact their
external services. Enforce the fixture boundary in the tool implementation, including
symlinks/path mappings; prompt instructions alone do not provide read isolation.

Pilot starting limits: up to two tool-result cycles per selector, **12 inspection
calls and 96 KiB of returned evidence across the entire preparation**, and a 60-second
preparation watchdog. Batch independent reads; apply aggregate caps consistently to
unified/grouped arms with stable batch admission order. Shared grounding spends from
the same allowance. Report truncation and limits explicitly. Calibrate once on the
pilot, then freeze; these are experiment controls, not proposed product limits or
acceptable user-facing latency. Read caches start empty per preparation.

## 4. Run protocol and measurements

1. **Preflight gate:** on 2–3 owned fixtures from the development set, verify complete
   inventories, file/tool isolation and the named Luna route: structured selection,
   supported reasoning, streamed terminal/error handling and inspection protocol.
   Verify returned model identity where available. Do not alter global configuration
   or silently change account/provider. Stop on unresolved completeness/protocol issues
   before building the full corpus/runner. Failure blocks execution, not this plan.
2. **Pilot gate:** use six development cases to debug extraction, labels and the runner,
   choose one supported reasoning setting and tune only documented bounds/wording.
   Freeze prompts, arms, trigger, policy, ordering procedure and scoring manifests.
   Do not run the full comparison until the data and protocol are credible.
3. **Paired comparison:** three repeats per case/arm on the 12-case comparison block,
   counterbalanced arm order within time blocks. Same settings and candidate ordering
   per pair; vary ordering with a recorded shuffle across repeats to reveal position
   bias. Fixed seeds only if supported; determinism is not assumed. Run one preparation
   at a time, allowing concurrency *inside* grouped arms. Label this finalist selection,
   not final confirmation. Report provider limits and actual overlap/queueing.
4. **Fresh confirmation:** freeze up to two quality-qualified finalists without changing
   their prompts/policy. Run both and keep-all on all six previously untouched cases,
   three paired repeats, each preparation immediately followed by its own bounded main
   continuation. Use original task facts, not historical answers. Capture main dispatch,
   first token and useful milestone in the **same** run; never combine averages from
   unrelated preparation and main-model runs. Do not tune on this block. No recommendation
   is confirmed until it passes here; revision requires a new untouched case block.

Record timestamps for admission, input loading, queue entry/provider dispatch,
first/last response, each tool batch, final validation and ready-to-send context.
`T_preparation` ends at ready-to-send and is **not user-facing startup latency**.
Three concurrent calls are measured by wall time, not summed durations. Confirmation
also records original admission → main dispatch → first token → useful milestone,
including actual final context processing, queueing and all model/tool rounds.
First token is a latency observation, not success. Also report:

- Required/obligation misses per domain and per task, before and after closure;
  valid alternative coverage; irrelevant retained tokens; useful/uncertain retention.
- Actual final context size, including schemas, guidance, discovery instructions
  and wrapper overhead—not merely the selector's compact metadata payload.
- Inference rounds, inspections, duplicated reads, abstentions/ambiguities, invalid
  IDs/output, fallback/timeout/error rate, and full-input overflow.
- Provider input/output/reasoning/cache token fields where reported. Distinguish
  unavailable values from zero, token estimates from reported usage, and nominal
  pricing from actual billed cost. Record all failed/retried calls too.
- Median, observed p95 and maximum preparation/startup/milestone latency, paired
  differences and per-stratum outcomes. Repeats of one task are not independent tasks:
  use case-level paired summaries/uncertainty. With 12 comparison and 6 confirmation
  tasks, tails are exploratory, not an SLA; show individual task results too.
- Warm/unknown provider cache state and connection warm-up. Randomize/counterbalance
  order; do not claim a provider cold cache can be forced if it cannot. Record
  unrelated throttling. Multi-child burst/load testing is a later integration study,
  not a prerequisite for this first selection comparison.

No retry on semantic disagreement/invalid selection. At most one documented retry
for a clearly transient transport failure within the preparation deadline; preserve
both attempts and label them. Stop a systematically broken arm after three consecutive
infrastructure/protocol failures, diagnose it, then restart under a new manifest.
Do not select only successful samples or silently delete censored latency observations.

## 5. Bounded downstream recovery check

Selection accuracy alone cannot establish startup value. For the six untouched
confirmation cases, measure time from original admission to a **predeclared useful
readiness milestone**: for example locating the intended plan and identifying the
correct next action, with explicit checks for obligations observable at this point.
First token or “I'll investigate” is not success. Use the same main model/settings
and recovery affordances for every arm/control. This can falsify early readiness;
it cannot establish full task quality, eventual instruction compliance or total task
latency. Obligations requiring edits/tests remain unverified, not implicitly passed.

The continuation can inspect real fixture files and search/recover exact candidates.
On an unqualified native-append route, include the stable invocation wrapper from
its first request and append recovered definitions. Record both outer and resolved
identities. Never silently mutate the native initial tools list or pretend a fake
adapter proves native append. Tool search hosted by a provider is not required.

Initially allow up to three main-model responses; record incomplete/censored outcomes
rather than calling them failures of the task itself. Stop before any real side-effect
call. A correctly formed intended call can be a declared milestone, but is **not**
successful execution. This checks early task readiness/recovery, not full task completion.
Record model-visible error handling and extra recovery time/tokens. Include a deliberately
omitted recoverable capability as a separate recovery calibration, not in selection scores.

Do not fabricate a general guidance-recovery mechanism if only tools/skills are
recoverable. Record what is actually supported; missed instruction obligations can
be irreversible before recovery. The case rubric must not forgive them merely because
the file could theoretically be found later.

## 6. Keep the research harness small

Before trusting measurements, check the harness's own boundary behavior: immutable
inputs, evaluator/file isolation, ID validation, skip-empty behavior, total evidence
accounting and deadline/Stop cancellation that joins outstanding requests/reads.
Use deterministic fixture checks, not live calls to test every error branch.

One failed selector must leave an identifiable per-domain failure. For the experiment,
fall back to all eligible candidates in that domain if they fit; otherwise record
preparation failure. Cancellation never becomes fallback. Score raw failure and final
fallback context separately; keep raw retention, dependency closure and fallback labels
so a successful keep-all fallback cannot masquerade as accurate pruning.

Do not build an experimental session/storage framework. Reuse Urd request-client and
reader seams only where genuinely simpler. File isolation, result recording and joined
cleanup belong to the small runner; source policy/selection are data, not new sessions.
Any eventual checked-in tests obey the repository's size budget rather than disguising
helpers as application code.

**Defer production guarantees to the actual Urd owner:** admitted-input/Stop races,
late-write fencing, durable selection/disclosure commits, reopen and compaction,
provider-native reconstruction, child cancellation and multi-child burst behavior.
This study may identify requirements for those tests but cannot qualify them through
a substitute owner. If task-completion evidence is later needed, scope a separate
executable-fixture study with isolated effects and deterministic outcome checks;
do not extend this read-only replay into historical side-effect execution.

## 7. Decision rule and deliverables

Before the comparison block, freeze the criteria above and a pilot-derived meaningful
latency difference; report that value in the manifest. Keep confirmation cases untouched
until finalist selection is complete. Quality gates:

- No protected-capability loss, future-context leakage or unsupported side effect.
- No unresolved critical instruction/startup-requirement miss for a recommended arm.
  Fixing an arm after evaluation requires fresh held-out cases, not relabeling misses.
- Report every noncritical miss and recovery burden; an aggregate average must not
  hide a failing domain or stratum. Keep-all establishes an omission floor, not proof
  that every possible instruction was understood or obeyed.

Among qualifying arms, recommend the simplest non-dominated **preparation bundle for
further integration**, using fresh confirmation's startup/readiness latency and retained
context. Do not claim a startup winner from preparation time alone. If recovery erases
preparation savings, do not call it a readiness improvement. A passing read-only milestone
is not positive proof of task completion or eventual instruction compliance. Report
unobserved obligations and cap the claim to the tested inputs/model and early behavior.

If differences are within observed noise, prefer fewer calls/less coordination; collect
a targeted fresh case block only where uncertainty changes the decision. No spend cap
does not justify an endless benchmark. A valid result is “none yet qualifies” or
“insufficient historical evidence,” with a specific next experiment—not a forced winner.

Deliver:

1. Private case/run manifests and reproducible requests/results with integrity hashes.
2. A short sanitized report: fidelity/sampling, model/config, per-domain quality,
   paired latency/usage, failures, representative misses, and limits of inference.
3. Measured trade-offs and behavioral requirements for source loading, shared evidence,
   selection and early recovery. These inform subsequent design; they do not settle
   production classes, storage/cancellation ownership, adapter fidelity or cache savings.

No application build/staging is needed for this documentation-only deliverable. The
later experiment neither migrates Pie data into Urd nor changes active user profiles.

## Review record

Kimi K3.1 Pro (`ollama/kimi-k3.1-pro:cloud`, high reasoning) completed an independent
read-only adversarial review on 2026-11-15. It read this plan and relevant Urd contracts;
it did not run replay experiments or inspect private transcripts. Eight findings led to:

| Finding | Disposition |
| --- | --- |
| Transcript hindsight can contaminate labels | Seal task-only annotations first; historical use is separate evidence; reviewed corrections need task-visible citations. |
| Fidelity tiers do not ensure complete catalogs | Independent completeness gate; incomplete historical cases are diagnostic-only, not hard-score/control inputs. |
| Guidance policy too vague to score | Require deterministic per-case resolver before labels; whole-file obligation rubric; no answer-key-based protection. |
| U1/G1 confound grouping and evidence distribution | Treat them as whole preparation bundles; reserve causal grouping claims for controlled inputs and report cap starvation. |
| Ready-to-send is not startup latency | Separate preparation timing from same-run main dispatch/first-token/readiness measurements. |
| Finalists selected and judged on reused holdout | Separate 6 development, 12 comparison and 6 untouched confirmation tasks. |
| Read-only continuation cannot prove full success | Restrict claims to early readiness/observable recovery; mark later obligations unverified. |
| Bespoke lifecycle/burst work over-scopes replay | Remove substitute durable owner and defer production integration/load qualification. |

Kimi's focused recheck found the revised plan adequate with no material validity
blocker. Its remaining wording correction was applied to question 6: the experiment
informs later integration requirements rather than settling production ownership.
The review transcript's assistant metadata confirms the named Kimi route was used.
No experiment results, Luna preflight or production implementation are claimed.
