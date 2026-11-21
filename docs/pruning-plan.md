# Starting-context pruning and title-generation plan

**2026-11-15 — active design plan, not implementation approval.**

This is the canonical record of the pruning and parallel title-generation direction.
Owner-selected requirements are separated from working proposals and unresolved details.
It does not
approve production APIs, a class hierarchy, new experiments or implementation work.
Existing [architecture constraints](ARCHITECTURE.md) still apply.

**Accepted context cleanup (2026-11-21):** current composition uses exactly three fixed
C# guidance files under `context/guidance/`: `SharedAgentGuidance`,
`PrimarySessionGuidance` and `SubagentSessionGuidance`. `StartingContextBuilder` emits
shared guidance, applicable role guidance, runtime directory context, selected skills,
then scoped discovered documents. `SelectedSkillsContext` under `context/runtime/`
renders selected skills. There is no `StartingContextContribution` abstraction,
reflection-based discovery or configurable guidance registration. `RequestContextEstimate`
belongs under `execution/`. This cleanup changes neither pruning behavior nor context
snapshot preparation, activation or reuse; the [context README](../source/backend/harness/context/README.md)
owns resource discovery details.

## Selected direction

Run three category-specific pruning conversations in parallel:

| Category | Selection scope |
| --- | --- |
| Skills | The discoverable skill catalog, not one conversation per skill |
| Additional instructions | Instruction documents such as `AGENTS.md` and append-system resources |
| Tools | The discoverable tool catalog |

Each conversation sees its whole unprotected category and can use basic tools,
including `read` and `bash`, to explore when necessary. Exploration is optional,
not a required preliminary stage. Keep useful capabilities rather than aggressively
pruning uncertain candidates: losing something useful is the more costly error.
Each pruning conversation independently decides what to inspect. Do not pool its
exploratory findings or selection decisions into sibling conversations; sharing
execution code does not mean sharing investigation or conversation state.

**Selector task input decision:** each conversation receives instructions explaining
its category-selection task, its category candidates and the following task input:

| Preparation boundary | Task input for each pruning conversation |
| --- | --- |
| First prompt in a fresh session | The user prompt |
| Automatic resumption after successful compaction | The compaction summary in place of a user prompt |
| Manual continuation after successful compaction | The compaction summary followed by the new user message |

For either post-compaction path, omit retained continuation context from pruning
inputs. This does not remove that context from the main agent's continuation.
Do not require a shared, preassembled grounding bundle or pool exploratory findings.

**Exploration decision:** do not add pruning-specific time, turn, token or tool-call
budgets. Each conversation decides how much exploration it needs and when to finish.
Existing provider/tool limits, current permissions, parent Stop and owned-work cleanup
still apply; this is not permission to bypass execution safeguards or add tools.
Without a pruning budget, preparation has no pruning-specific upper latency bound.

**Built-in instruction decision:** Urd's built-in instructions are always included
where applicable and never enter pruning, including built-in working defaults and
preferences. Existing role/scope applicability and ordering still apply. Additional
instruction documents remain pruning candidates unless protected; an `AGENTS.md` or
append-system resource is not automatically protected merely because of its format.

**Instruction input decision (2026-11-15):** supply the instruction pruner with the
full contents of every unprotected candidate document upfront, not just paths or
summaries requiring follow-up reads. For a confirmed instruction-context overflow,
retry with the largest unprotected files omitted first (by UTF-8 body size)
and surface a cumulative warning. Protected documents remain excluded. This does not
select full skill-body loading or change the other categories' input contracts.

A historical Windows test with a scripted provider rejected the first selection request for
1,900 files (1,226,256 UTF-8 bytes of file bodies). The retry omitted the largest file;
1,899 files reached the main chat, with a persisted warning. This exercises the recovery path,
not a provider token limit or a guarantee that the main chat will fit. See
`git show 774461f:docs/instruction-resource-catalog-plan.md` for the original record.

**Model selection decision:** all four purposes default to Small; resolve the selected
bucket through its configured mapping or an explicit exact provider/model override. If
unavailable, skip with a warning and never substitute chat; use the title's first-prompt
fallback when it yields a title. See [Selection failure](#selection-failure) for category fallbacks.
This auxiliary setting does not add exact-model overrides to agent-created session APIs.

The implementation must be modular, extendable and share common execution rather than
duplicate pruning loops for the three categories. Separate conversations do not imply
separate engines or new full-session subtypes.

## Parallel title generation

**Owner direction:** include title generation in the same prepass planning as pruning.
Run it in parallel with the pruning conversations, using shared execution rather than
another auxiliary-job framework. It is a distinct operation, not a fourth pruning
category or a consumer of `StartingResourcePruningResult<T>`.

**Title trigger decision:** generate only after the first message of a session, as
part of its initial prepass. No later message, compaction/resumption, reopen/restart,
model change or preference change triggers title generation. This is a session-level
boundary, not a once-per-host-lifecycle opportunity. It does not add a later
regeneration trigger or a manual naming/renaming feature.

**Nonblocking title decision:** once pruning has settled safely, start the main agent
without waiting for unfinished title generation. Let title work complete separately
and publish through the existing guarded metadata path; nonblocking does not detach
it from session cancellation or owned-tool cleanup.

**Communication clarification (2026-11-16):** session titles are immutable addressing keys.
Subagent creation generates the child's title during its pruning/title prepass; the caller does
not supply one. Exact duplicate eligible titles are an ambiguity error. The nonblocking first
message behavior above is unchanged for primary sessions; do not infer a tool-facing title from
a pending generation attempt. See [agent communication](agent-communication-design.md#addressing).

- Supply title-generation instructions and the user prompt.
- Permit optional `read`/`bash` exploration, independently of the pruning conversations.
- **Title length decision:** generated titles have a strict maximum of **25 characters,
  including spaces**. Enforce the limit in application validation rather than relying
  solely on prompt wording. Duplicate suffixes are included in the 25-character maximum.
- Reuse shared request/tool-turn mechanics and existing guarded recording, current
  permissions, cancellation and owned-tool cleanup. Keep conversation state, output
  validation and metadata publication separate from pruning and main-agent history.

**Superseded title validation/failure decision (2026-11-15):** allow one corrective
response when generated output is invalid, then leave the placeholder if generation fails.
This failure fallback is superseded by the next decision and
[agent communication](agent-communication-design.md#addressing); the one-correction limit
and cancellation/recording/cleanup safeguards remain.

**Selected failure fallback (2026-11-16):** titles are communication addresses, so a
session cannot remain on a placeholder. If generation (including its one correction) fails,
use the session's first user prompt (a subagent's task) truncated to the 25-character maximum
as its final title. Duplicate titles take the lowest free ` (n)` suffix; shorten the base so
the final title remains within 25 characters. This replaces the superseded fallback above
and the older implementation notes below. See
[agent communication](agent-communication-design.md#addressing).

**Pie reference:** Pie counts the base title using JavaScript string length and targets
15–20 characters. Urd instead uses the selected 25-character maximum and one correction;
its title policy also uses immutable addresses and collision suffixes within that maximum
(see [agent communication](agent-communication-design.md#addressing)). Pie's title
generator is request-only; Urd's optional exploration is an explicit addition.

**Implementation details:** specify the Unicode counting rule consistently in
validation and tests. Urd has no manual session-naming flow; do not add one or design
manual-name arbitration as part of this feature. Existing first-prompt-preview-derived
UI titles are not manual names; align that current naming path with generated titles
and the selected first-prompt fallback after title-generation failure. Placeholder titles are not addressable; see
[agent communication](agent-communication-design.md#addressing). Post-compaction pruning
does not regenerate titles. The existing three-row pruning UI decision does not select a
title row.

## Pruning triggers

**Owner decision (2026-11-15):** category pruning runs at exactly two boundaries:

1. On the first prompt in a fresh session, before its first main-agent request.
2. On the first message or continuation after successful compaction, before the
   next main-agent request.

Compaction completion alone does not start pruning. If the session stays idle, wait
for that next message or continuation. Failed or cancelled compaction does not create
this boundary.

No other event independently triggers pruning: ordinary messages, reopening or restart,
model/provider changes, settings/protection changes, file edits or resource reloads.
There is no standalone manual re-pruning trigger. A message after reopening can still
be the first one after compaction; reopening itself is not the trigger.

Capability recovery remains distinct: it can append an eligible capability without
rerunning category selection. This decision does not add new recovery behavior.

Rationale: preserve model-visible prefixes between these boundaries and avoid cache
loss and its associated provider costs. Actual cache effects depend on the emitted
context and provider; selection computation alone does not necessarily invalidate
an unchanged main-agent prefix.

## Protection happens before selection

Settings also has protected-item lists for each category. Protect **`bash`, `read`,
`write` and `edit`** by default, not as non-removable pins. Users can remove those
protections if they wish and can add protected tools, skills and instruction documents.
An unprotected core tool becomes an ordinary pruning candidate; its availability to
the main agent is no longer guaranteed unless runtime protection applies.

**Post-compaction runtime protection:** tools with currently running background calls
are protected for that preparation, in both automatic resumption and manual
continuation. Exclude their declarations before constructing tool-pruning candidates,
then retain them when composing the selected context. This is runtime protection,
independent of the user's editable defaults; it does not cancel or restart those calls,
or bypass current permissions. Finishing a background call is not a new pruning trigger.

Protected entries bypass pruning and remain available to the main session. Exclude
them before constructing pruning inputs, not merely by restoring them after selection:

- Do not send their candidate entries or a separate list of protected IDs.
- Do not reintroduce protected documents through an unfiltered shared-guidance input.
- Preserve protections when applying selection results too.

The pruner's exploration tools are a separate surface: it can still see and invoke
`read` and `bash`, independently of their protection in the main-session catalog. Protection
is not filesystem secrecy or an access sandbox; exploration could encounter a file.
Likewise, an inspection-only brief does not make `bash` technically read-only.

Protection does not change resource semantics. A protected skill stays listed; its
body is still read on demand. A protected document retains its normal scope and
precedence, rather than being injected into unrelated projects.

## Project resource discovery

**Implemented direction (2026-11-18):** project instructions and skills are discovered
from matching files in the working directory and descendants, without requiring Git. No
ancestor or sibling project guidance is included; Urd's packaged skills remain available.
This supersedes the earlier Git-only discovery decision, not the pruning triggers or
protection contract. The [context guide](../source/backend/harness/context/README.md#project-instruction-documents)
owns filename recognition, the path-only catalog, and scanner and watcher behavior.

**Document scope decision:** include agent-facing Markdown instruction resources,
including `AGENTS.md` and append-system resources. Exclude files specific to other
harnesses, such as `CLAUDE.md` and Copilot instructions. This scope is not a request to
inject every Markdown file: ordinary project documentation remains available for
independent exploration, and skills remain their own category. Changing how files are
found does not turn ordinary Markdown or another harness's instructions into candidates.

## Settings and picker direction

| Category | Model | Protected items |
| --- | --- | --- |
| Tools | Small bucket (default) or specific model | Core tools plus user selections; Add… |
| Skills | Small bucket (default) or specific model | User selections; Add… |
| Additional instructions | Small bucket (default) or specific model | User selections; Add… |

The picker lists every discoverable skill or instruction document for the **default
working directory**. Tools come from the registered catalog and are expected to be
more stable.

Discussed UI/data proposals, not a final UI or storage contract:

- Reuse session-preparation discovery rather than a settings-only scanner; list
  recognised instruction resources rather than treating every Markdown file as a
  system prompt.
- Search by name/source; show paths so similarly named entries are distinguishable.
- Show the directory being inspected and allow refresh.
- Use stable identities rather than display names for saved protections.
- Refresh choices when the default directory changes without silently deleting saved
  protections; show missing items as unavailable rather than matching a different item.
- Apply protections to resources discovered and applicable in the actual session's
  context; the default-directory picker is not a reason to change that session's scope.

## Pruning display — initial direction

Start with three rows, one for each pruning conversation: skills, instructions and
tools. The owner wants to revisit the detailed UI against a working implementation,
rather than settle speculative layout and interactions now. A single expandable
preparation row is not the selected starting point.

## Working composition sketch

This is the proposed shape from the discussion, not a settled runtime/API contract.

```text
Discover and resolve applicable resources
                  |
          Separate protected entries
             /                 \
     Protected entries      Unprotected candidates
             |              /         |          \
             |           Skills   Instructions   Tools
             |              \         |          /
             |               Parallel selection
             |                       |
             +---------- Validate and combine
                                     |
                           Main starting context
```

Each selector has its own category-specific brief and candidate inputs, excluding
protected entries. Tools and skills do not wait for the instruction selector's output;
each chooses its own exploratory reads. Each receives its selection instructions,
the boundary-specific task input defined above and its category candidates, not a
shared investigation bundle.

## High-level structure proposal

**Historical proposal (2026-11-16), superseded for context composition by the accepted
cleanup above.** The remaining proposal records design history, not the current context
API or permission to create speculative APIs. Paths are relative to
`source/backend/harness/` unless stated otherwise.

The shared discoverer/single typed pruner direction was accepted; the concrete structure
below was proposed at that time, not approved as the current context class model.

### Historical naming decisions

| Concept | Term selected in the historical proposal |
| --- | --- |
| Rule-bearing content, whether built-in or loaded from files | **Instructions**, rather than guidance or system prompts |
| One identifiable, orderable contribution of text intended for the session's starting context | **`StartingContextContribution`**, a proposal superseded by explicit composition; no such abstraction is in the current API |
| Project-owned result types from shared instruction-resource discovery | **`Discovered` prefix** |
| A loaded instruction document returned by resource discovery | **`DiscoveredInstructionDocument`** |
| A skill's metadata returned by resource discovery | **`DiscoveredSkill`**, replacing the existing `Skill` name |
| The umbrella covering instruction documents and skills | **Instruction resources** |
| The shared operation that discovers and loads instruction resources | **`InstructionResourceDiscovery`** |
| The shared selector for tools, skills and instructions intended for starting context | **`StartingResourcePruner`** |
| A category pruning outcome, carrying retained original items, success/fallback status and diagnostics | **`StartingResourcePruningResult<T>`** |
| The component coordinating discovery, protection, pruning and construction of starting context | **`StartingContextBuilder`**, replacing the existing `StartingContextManager` name |

The proposal modeled fixed and data-dependent text as `StartingContextContribution`
instances. That abstraction and its order/applicability/rendering contract are superseded:
current composition uses the three fixed guidance files and the explicit order stated above,
without reflection or configurable registration.

`Discovered` identifies provenance, not a temporary processing state or an object that
performs discovery. Retained resources keep these types after pruning. Instruction
documents carry their full loaded text; skills still carry metadata for on-demand reading.
This convention does not require separate raw, discovered and selected wrapper types.

Instruction resources comprise general/project instruction documents and skills, which
provide specialised task instructions. This umbrella excludes registered tools and
runtime-generated context facts. `InstructionResourceDiscovery` owns finding/loading
these resources, not pruning or context composition. The terminology does not require
a new common resource base class.

`StartingResourcePruner` handles all three categories through separate invocations of
one implementation. It selects from supplied, unprotected candidates; it does not
discover resources, prune conversation history or assemble the final context. Its name
identifies the resources' destination, not a once-per-session execution limit. The
owning preparation still controls the agreed fresh-session/post-compaction triggers.

Other names in this table describe the historical proposal; they should not be read as
claims about the current context API or as permission to refactor source code.

### Files and types

```text
context/
  guidance/
    SharedAgentGuidance.cs           fixed shared guidance
    PrimarySessionGuidance.cs        fixed primary-session guidance
    SubagentSessionGuidance.cs       fixed subagent-session guidance
  runtime/
    StartingContextBuilder.cs        explicitly composes guidance, runtime context, skills and scoped documents
    SelectedSkillsContext.cs         formats supplied skills; no discovery
    StartingContext.cs               carries the composed context
    InstructionResourceDiscovery.cs  discovers skills and instruction documents
    DiscoveredInstructionResources.cs typed discovered collections
    DiscoveredInstructionDocument.cs loaded, scoped file-backed document
    DiscoveredSkill.cs               skill metadata
    SkillFrontmatter.cs              skill metadata parser
  pruning/
    StartingResourcePruner.cs        three typed selections, one selection core
    PruningContext.cs                optional proposed packaging, not a required type
    StartingResourcePruningResult.cs retained items and outcome/diagnostic
execution/
  ConversationRunner.cs              proposed shared stream/tool-turn extraction
  RequestContextEstimate.cs          request-scoped estimation
  TitleGenerator.cs                  proposed title operation using the same mechanics
```

`InstructionResourceDiscovery` absorbs discovery/loading from `SkillCatalog`; remove
that old owner once its callers move, rather than retain two discovery pipelines.
Keep kind-specific parsing private to discovery and reuse `SkillFrontmatter`. Preserve
existing packaged-skill and project-override rules. Tools remain registered resources
from `ToolCatalog`, not files found by the discoverer.

The historical proposal's `DiscoveredInstructionResources` aggregate contains
`IReadOnlyList<DiscoveredSkill>` and `IReadOnlyList<DiscoveredInstructionDocument>`.
Its proposed `DiscoveredInstructionDocument : StartingContextContribution` model is not
current: documents remain scoped resources, while `StartingContextBuilder` explicitly
composes them after fixed guidance, runtime directory context and selected skills.

### Proposed method surface

```csharp
// InstructionResourceDiscovery
Task<DiscoveredInstructionResources> FindAsync(
    string workingDirectory, CancellationToken cancellation);

// StartingResourcePruner: one instance for one preparation
StartingResourcePruner(PruningContext context, ConversationRunner execution);

Task<StartingResourcePruningResult<DiscoveredInstructionDocument>> PruneAsync(
    IReadOnlyList<DiscoveredInstructionDocument> instructions, CancellationToken cancellation);
Task<StartingResourcePruningResult<AIFunctionDeclaration>> PruneAsync(
    IReadOnlyList<AIFunctionDeclaration> tools, CancellationToken cancellation);
Task<StartingResourcePruningResult<DiscoveredSkill>> PruneAsync(
    IReadOnlyList<DiscoveredSkill> skills, CancellationToken cancellation);
```

These are proposed shapes, not compiled declarations or frozen APIs. Use the existing
model-facing tool declarations, not a new tool wrapper type.
`StartingResourcePruningResult<T>` carries retained original items plus the outcome and
diagnostic needed to distinguish successful selection from keep-all fallback.
Cancellation and fatal recording/cleanup failures propagate through the owning execution
instead of becoming successful result objects.

The overloads choose their category brief and resolved model, then delegate to one
private selection core. Keep prompt formatting with `StartingResourcePruner` initially. No
`SkillPruner`/`ToolPruner`/`InstructionPruner` classes, resource-handler registry or
plugin hierarchy are needed.

### Existing owners and shared prerequisites

- `StartingContextBuilder` is the renamed existing manager, not an additional coordinator.
  It coordinates discovery/protection/pruning/composition, using private helpers. Capture
  models and task context for a preparation; construct its pruner and launch the three
  calls before awaiting their results. Merge those results with protected resources in
  source/precedence order. The builder does not own session admission, Stop or durable writes.
- `execution/ConversationRunner.cs` is a proposed extraction of shared streamed-response
  validation and tool-turn mechanics, not a new provider client. It is supplied with the
  parent execution's guarded recording/tool authority; each pruning invocation has its
  own conversation/request scope. Main execution reuses the same mechanics. Primary
  steering, compaction and settlement remain outside this class; its exact execution
  API still needs the extraction design, rather than speculative interfaces here.
- `providers/ModelSelector.cs` is a shared prerequisite: bucket or specific provider/model
  choice resolves to the existing `LlmSelection`. This is now implemented for auxiliary
  purposes, including persisted `small`/`medium`/`large` mappings in the current
  implementation.
  Delegated-agent selection/fallback still depends on the unimplemented delegation runtime.
  Reuse the same owner for delegated work, not pruning-only buckets. Resolve model choices
  once per preparation; `PruningContext` is only a proposed container. Do not silently
  inherit the main chat's model.
- `SessionExecution` owns preparation and title work; `SessionExecutionDriver` joins owned
  work before settlement/disposal. Extend guarded recording with distinct pruning/title
  conversation/request scopes and `StartingContextStorage` with selected contexts at
  allowed compaction boundaries. Do not make the builder a second persistence owner.
- Extend `UserPreferences`, `PreferenceManager`, `PreferencesStorage` and their existing
  routes; reuse settings and `ModelPicker`. A small `PruningRows.tsx` under `source/ui/chat/prepass/`
  renders recorded category status. Detailed UI remains deferred.

**Revised after owner feedback:** shared discovery is a resource-level responsibility,
not merely a Git helper below separate discoverers. `InstructionResourceDiscovery` owns
finding/loading skills and instruction documents; `StartingResourcePruner` owns selection;
`StartingContextBuilder` connects them and handles protection/composition. Resource kinds
retain distinct parsing rules and typed values without requiring separate top-level
discoverer classes.

Construct one pruner for a preparation with its immutable task context, model choices
and owned-execution dependencies. Each overload chooses the corresponding category
brief/model and calls the same core; histories, request identities and progress are
local to each invocation, so the three calls can run concurrently. The public result
contains original retained items plus outcome/diagnostics; model-facing IDs stay an
internal selection detail. Do not silently equate a failed keep-all fallback with a
successful decision to keep every item.

`SelectedSkillsContext` under `context/runtime/` formats the selected skill list instead
of rediscovering everything. Settings and preparation use `InstructionResourceDiscovery`;
the shared registered tool catalog remains the tool source. If shared execution or bucket
resolution lands through another feature first, reuse it; these are responsibility boundaries,
not competing implementations.

### Proposed data flow

1. The owning execution checks whether the current context boundary already has a saved
   selection. Use the session's initial boundary or the latest successful compaction ID,
   not every history change, model change or preference change.
2. At an allowed preparation boundary, capture the task context, discovery results,
   preferences and resolved models once. At a post-compaction boundary, include tools
   with running background calls in runtime protection. Separate protected entries
   before building selector inputs. Each selector has its own conversation and category brief.
3. Call the same `StartingResourcePruner` for the three typed sets in parallel. Each may
   use the shared tool-capable execution path for exploration, but never recursively
   prune itself.
4. Validate returned IDs against that category's candidate set. Keep original resources
   in stable source/precedence order; the model does not rewrite documents or schemas.
   Apply ordinary per-category keep-all fallback and combine with protected entries.
5. Once all owned pruning work has settled safely, the execution records/uses the new
   context at the main-request boundary. Ordinary requests and reopen reuse the saved
   selection. Compaction alone does not launch selectors.

**Input contract:** use the boundary-specific task input in the selected direction:
fresh user prompt, compaction summary alone for automatic resumption, or summary
followed by the new user message for manual post-compaction continuation. Omit retained
continuation context from post-compaction pruning inputs. Do not copy the main system
instructions and thereby reintroduce protected documents.
Candidate payloads are skill name/description with paths grouped by identical metadata,
existing tool declarations, and full unprotected instruction text with source/scope.
Every skill file remains independently selectable by its exact path, even when it shares
metadata with other files. Treat candidate contents as selection material. The model returns
kept candidate IDs, not rewritten resources; the pruner validates/maps them back to the
original typed resources. Invalid output is an ordinary selection failure.

**Selected instruction boundary:** Urd's built-in instructions form fixed base context
and bypass pruning, including built-in working defaults/preferences. Additional discovered
instruction documents are candidates unless protected. Current composition emits shared
guidance, applicable role guidance, runtime directory context, selected skills through
`SelectedSkillsContext`, then scoped documents. The composition API is fixed as described
above; this does not change which resources are candidates or how selection is persisted.

### Integration constraints the design must address

- **Not three full sessions.** The rows represent owned pruning conversations, not new
  `SessionBase` subtypes, independent chats or another auxiliary-job framework. The
  earlier request-only pruning sketch predates the selected `read`/`bash` exploration:
  compaction can remain request-only; tool-capable pruning and title generation need
  shared turn execution as well as `IChatClient`.
- **Separate records, same authority.** Parallel selectors need distinct conversation and
  request identities, isolated histories and tool-call/result pairing. Reuse the parent's
  guarded execution authority and current tool permissions. Pruning messages/results must
  not enter main-agent history or consume its pending user input. Track auxiliary provider
  attempts separately: no main-agent request does not mean no provider contact occurred.
  Preserve usage attribution to the parent and identify the pruning category.
- **Saved context changes deliberately.** Current storage saves one context per session
  and ignores later saves. It must support a selected snapshot associated with the allowed
  boundary and exact ordered content on reopen. Preserve primary send-attempt/input-placement
  atomicity; auxiliary request starts cannot use that path unchanged. Interrupted pruning
  or its tools must not be replayed merely by loading a session. Exact prepared-versus-active
  snapshot commit details remain an implementation-design item.
- **Compaction planning is not pruning.** Existing `EnsureStartingContext()` also runs when
  planning compaction. Separate access to the current composed context from asynchronous
  selection, otherwise manual compaction would accidentally become a pruning trigger.
  Automatic compaction must finish before preparation for the following main request.
- **Fallback is not a catch-all.** Provider/selection failures, including existing
  provider/tool timeouts, retain that category only after its owned work settles safely.
  Do not introduce a pruning-specific budget or timeout. Parent Stop cancels and joins
  all branches. Uncertain tool effects, failed durable recording or failed cleanup keep
  the existing execution fence; they must not become keep-all-and-continue. A timeout
  requests cancellation, not proof that tools have stopped.

### Implementation milestones

**Owner approval (2026-11-16):** begin with milestone 1, then continue the staged
implementation through completion, providing verified progress updates and raising
material behavior changes for review. Earlier planning-only statements above preserve
their original approval state; they do not prohibit the subsequently approved work.
This does not authorize live-provider evaluation, profile reset or host replacement.
Each milestone should be a locally verified, task-scoped commit or small cohesive set
of commits. Do not enable a partly integrated prepass in normal sessions.

**Implementation checkpoint (2026-11-16):** all six milestones are implemented in
source. The fully integrated prepass selects three categories in parallel and generates
the one-shot title independently; all work remains execution-owned. Boundary snapshots,
schema 29, guarded status/title publication, model/protection Settings, three status rows
and authoritative backend tab titles are integrated. Unicode title counting uses scalar
values, including spaces; generated titles are not truncated or retried after the one
correction. Under the settled behavior, generation failure uses the first prompt truncated
to 25 characters. Configure the `small` bucket in Settings; an unresolvable category or
unavailable title choice runs that prepass on the run's own chat selection, including its
reasoning effort, and the preparation summaries report `usedSessionModel` for the browser.
No silent main-model inheritance is introduced: the stand-in is recorded, not hidden. A title
attempt that still fails keeps the placeholder (current implementation; superseded by the
settled fallback in
[agent communication](agent-communication-design.md#addressing)). The chat and placeholder
fallback notes are historical.

Earlier proposal examples in this document preserve design history, not a frozen API
or statements that these mechanisms remain unimplemented. Concrete discovery rules live
in the [context README](../source/backend/harness/context/README.md); implemented ownership
is described in [backend development](backend-development.md#starting-context) and
[execution design](execution-design.md#implemented-core). Verification uses fake/loopback
providers and owned temporary profiles; it does not establish pruning quality or a
winner over keep-all. The running host and live database have not been changed.

Final local verification: **579 Release backend tests**, **338 UI tests**, formatter,
type-aware lint, strict TypeScript, production UI build and the offline real-server/SDK
integration flow passed. Independent execution, storage, discovery/settings and title/UI
reviews found no remaining supported blockers. The physical-line budget is **30,812
test/helper lines / 42,279 application lines**. Assets and server are staged only under
isolated `bin/Verification/` outputs; no profile reset, installation or host activation.

**Implementation checkpoint (2026-11-17):** source now uses schema 31. Response headers
freeze model, effort and start time; preparation rows are boundary-anchored for tools,
skills, instructions and title. Pending response state is transient until a transcript
entry provides its anchor. On-demand detail pages hold up to 60 resources per page; provider-emitted reasoning
and output preview tails are bounded to 8,192 Unicode scalars. Candidate counts exclude
protected resources. Title failure still leaves the existing placeholder; the planned
first-prompt fallback has not been implemented.

Verification passed **590 Release backend tests**, **349 UI tests**, formatting, type-aware
lint, strict TypeScript, the production UI build and the offline real-server/SDK flow.
Manual browser acceptance covered desktop and 320px layouts, live reasoning, keyboard paging,
hover/focus/Escape, polling cleanup on close/hidden tabs, draft preservation, reload, delayed
title publication and keep-all failure fallback. Visual review approved the final captures.
The physical-line budget is **31,465 test/helper lines / 44,271 application lines**.
All provider calls used credential-free loopback fixtures and owned temporary profiles;
the existing host and databases were untouched. This is not live-model quality evidence.

| Milestone | Change boundary | Acceptance evidence |
| --- | --- | --- |
| 1. Shared execution and auxiliary recording | Extract reusable stream validation/tool-turn mechanics from `SessionExecution`, `LlmResponseAccumulator`/`LlmResponseWriter` and `SessionToolExecution`. Add isolated auxiliary conversation/request recording under the existing session authority. Keep main steering, compaction and settlement with their current owners. | Existing main-chat tests unchanged; interleaved auxiliary streams/calls cannot mix histories, place pending main input or repeat tools. Stop and save/cleanup failure barriers still hold. |
| 2. Model choices and resource discovery | Add shared bucket-or-specific-model resolution returning `LlmSelection`; persist the four independent auxiliary choices through existing preferences. Move skill discovery into `InstructionResourceDiscovery`, add document loading and make the skill contribution format supplied results. Apply the selected context type renames as callers move, without compatibility aliases. | Independent overrides, configured bucket resolution and unavailable-model handling; existing skill override rules; nested tracked versus untracked files; full document contents, exclusions and stable protection identities. |
| 3. Boundary-aware preparation/storage | Replace once-per-session context storage with exact ordered snapshots associated with the initial boundary or committed compaction ID. Split current-context access for compaction planning from async preparation. Add guarded auxiliary status and the durable initial title-attempt claim. | Correct fresh/automatic/manual task inputs; compaction alone never starts pruning; exact restoration on reopen; request-start/input-placement atomicity; failed writes never start provider contact. |
| 4. Three-category pruning | Implement `StartingResourcePruner` with one selection core and three independent calls. `StartingContextBuilder` separates protection, invokes pruning, validates/maps IDs and composes results. Retain original text/schemas in source/precedence order. | Protected inputs excluded, built-ins retained, running-background-tool protection after compaction, parallel isolation, successful sibling selections survive category failure, Stop prevents main-agent startup. |
| 5. Nonblocking title generation | Add the separate tool-capable title operation to the first-message prepass. Persist/publish generated title metadata; remove the browser's first-prompt-derived naming path. Permit one invalid-output correction, then use the first prompt truncated to 25 characters if generation fails. | Main provider work/output can proceed while title work is held; 25-character validation; one correction only; failed generation produces the first-prompt fallback; no later-message/reopen regeneration; Stop and stale publication guards. |
| 6. Settings and prepass display | Extend existing preference routes/client/hook and Settings; reuse model catalogs and picker presentation for the four choices. Add protection pickers using shared discovery for the default directory and the three recorded pruning rows. Display backend title metadata in the existing tabs. | Independent saves and errors; missing entries do not silently rebind; core defaults removable; truthful row outcomes; title updates without disturbing tab order, focus or drafts. |

**Execution/recording details to establish in milestone 1:**

- The current recorder and request/tool storage are main-transcript-oriented. Reuse
  validation and guarded writes, not the main `StartRequest` input-placement path.
  Distinguish auxiliary requests and tool results durably, with category/purpose usage
  attribution, without introducing new session kinds or a general job framework.
- Give each invocation its own conversation history and explicit exploration surface.
  Offer `read`/`bash`, not the entire session catalog: that catalog also includes
  `list_pending_calls`/`cancel_pending_calls`, which could affect sibling/session work.
  Existing permissions still gate invocation; do not treat `bash` as technically read-only.
- The proposed simple ownership is the initial `SessionExecution` for title work as
  well as pruning. Extend driver Stop, failure drain and final settlement joins to
  include auxiliary provider tasks and their tools. A main response may finish while
  the title is pending; do not dispose its owner or abandon late recording/cleanup.
  This can keep the execution unsettled until owned title work completes, without
  delaying main-agent requests/output. No detached fire-and-forget task is acceptable.

**Storage/activation details to establish in milestone 3:**

- Resolve selection for one allowed boundary, then activate its immutable context
  alongside main request-start/input placement in the existing guarded transaction.
  Do not publish a partly selected context; only reuse a snapshot tied to the current
  initial/compaction boundary. Compaction planning reads the current context without
  triggering discovery/model selection.
- Record auxiliary attempts independently of main send-attempt provenance. Auxiliary
  provider contact must not turn a not-yet-sent main prompt into a sent prompt.
  Interrupted work is inspectable but is never automatically replayed on session load.
- Persist the title-attempt claim before its provider contact, linked to the first-message
  boundary. Reopen/restart must neither regenerate nor replay a naming tool. Record a
  successful title through the guarded session snapshot/change path, not browser-only state.
- Follow the [durable-data policy](../AGENTS.md) for database schema changes. Version/discard
  incompatible browser state. Never reset the user's database or profile as a verification
  shortcut; use owned temporary data.

**Bounded engineering choices, not another behavior questionnaire:**

- Before milestone 2 ships, make instruction recognition, source locations, nested scope,
  precedence and protection identities concrete in its design/tests. Preserve the selected
  agent-facing filename scope and other-harness exclusions as discovery changes; neither ingest
  arbitrary Markdown nor silently limit the feature to two filenames. Do not invent new
  user configuration locations without review.
- Use one resolver; do not guess model quality, widen providers or use the superseded chat stand-in.
- Specify title counting once for server validation and tests. Preserve the approved strict
  25-character maximum, one corrective response and first-prompt failure fallback; duplicate
  suffixes must also fit within 25 characters. Do not add manual naming features.
- Detailed row layout remains deferred until there is a working implementation. Share only
  mechanics with real callers; avoid speculative wrappers, registries or a new scheduler.

**Verification and handoff:** reuse `ExecutionFixture`, `ScriptedChatClient`,
`TestAgentTool` and deterministic `TestAsync.Signal` barriers. Extend the existing
execution/tool/starting-context/compaction tests and preference/UI model tests instead
of building a second test harness or asserting exact selector prose. Respect the hard
[test/helper size budget](testing.md), with one behavioral owner per guarantee.

At meaningful milestones, run the relevant focused tests first. Before final handoff,
run Release backend tests, UI static checks/unit tests, UI build, Release server build
and the offline real-server/SDK integration flow using the repository's
[backend](backend-development.md#prerequisite-and-commands) and
[browser](browser-development.md#toolchain-and-checks) commands. Use fake/loopback
providers and owned temporary profiles; these establish execution behavior, not live
model selection quality. Review non-trivial integration independently. Build/stage
verified artifacts only through the repository workflow without replacing an active
host or resetting live data. No benchmark or further external-provider evaluation is
part of this implementation plan.

## Selection failure

**Current owner direction (2026-11-19):** on ordinary failure, retain only protected
skills and instructions; retain all tools. Instruction overflow still retries with
largest files omitted first, but any later failure also retains only protected instructions.
This supersedes the earlier keep-all and overflow-fallback decisions below.

**Owner decision (2026-11-15):** if a category's pruner fails or times out, keep all
available entries in that category for the preparation and continue with successful
selections from the other categories. Surface the failure rather than reporting
successful pruning, but do not block the main agent merely because selection failed.
For example, failed tool pruning retains all available tools while successful skill
and instruction selections still apply.

**Approved exception:** a confirmed instruction-context overflow retries instruction
selection with the largest unprotected files omitted first (by UTF-8 body size).
Drop one, then two, then four on successive rejections; always try the smallest file
alone before dropping it. Surface a cumulative warning for the omissions, even after
success. A later fallback keeps the remaining files. Tools and skills are unchanged.

User cancellation stops the preparation; it does not activate keep-all-and-continue.
Existing current-permission and owned-work cleanup requirements still apply. This
settles the general fallback; the approved instruction-overflow exception above defines
that retry path, while other retry details and the precise failure UI remain unsettled.
The later no-budget decision rules out adding a pruning-specific timeout; existing
timeouts can still fail.

## Discussed working defaults

These remain implementation proposals, not separately settled contracts:

- Return kept candidate IDs rather than rewritten instruction documents; validate and
  combine them with protected entries in stable source/precedence order.
- Capture model choices when preparation begins; settings changes affect subsequent
  preparations rather than changing an in-flight run.

Existing Stop ownership still applies: cancel and join the owned pruning conversations
and their tools, without starting the main agent after cancellation. Preserve existing
prefix-stability, current-permission and capability-recovery boundaries; pruning does
not replace those mechanisms.

## Still unresolved

- Exact recognition/loading rules and non-project locations within the selected
  agent-facing Markdown scope, plus stored source identities. Other harnesses'
  instruction files remain excluded.
- Title integration/validation details listed under [parallel title generation](#parallel-title-generation).
- Remaining pruning-specific concrete classes/APIs and preference/boundary-context
  storage details. The reviewed sequence above guides subsequent slices; delivered source
  owns those APIs. Context composition itself is settled by the accepted cleanup above.
  `PruningContext` remains an optional packaging proposal, not a required shared
  investigation or a settled type/name.

**Owner steering (2026-11-15):** duplicate-name/project-override skill protection is
an edge case not worth further design time. Leave its handling to simple implementation;
this does not select name-based versus source-pinned protection.

The [classification evidence](pruning-classification-results.md) informs this choice;
it does not prove the complete three-category design is superior or qualify instruction
pruning with live exploration. No further evaluation or implementation is authorized by
this document alone.
