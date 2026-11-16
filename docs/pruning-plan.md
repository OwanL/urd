# Starting-context pruning and title-generation plan

**2026-11-15 — active design plan, not implementation approval.**

This is the canonical record of the pruning and parallel title-generation direction.
Owner-selected requirements are separated from working proposals and unresolved details.
It does not
approve production APIs, a class hierarchy, new experiments or implementation work.
Existing [architecture constraints](ARCHITECTURE.md) still apply.

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
summaries requiring follow-up reads. Protected documents remain excluded. This does
not select full skill-body loading or change the other categories' input contracts.

**Model selection decision:** each category uses the configured `small` model bucket
by default. In Settings, users can override that default with a specific provider/model
for each category, including a local-inference model. Store these choices in user
preferences. An override affects that category, not the shared `small` bucket itself.
This auxiliary-operation setting does not add an exact-model override to agent-created
session APIs.

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
boundary, not a once-per-host-lifecycle opportunity. It does not add a later manual
regeneration trigger; explicit/manual renaming is a separate operation.

**Nonblocking title decision:** once pruning has settled safely, start the main agent
without waiting for unfinished title generation. Let title work complete separately
and publish through the existing guarded metadata path; nonblocking does not detach
it from session cancellation or owned-tool cleanup.

**Deferred communication consideration:** session titles are intended to support
addressing such as “send message to <title>”. The owner explicitly deferred that
design. Missing-title windows, collisions, renames and address resolution belong to
future communication work, not a reason to delay the main agent now or introduce a
title registry in this slice. See [agent communication](agent-communication-design.md#deferred-title-based-addressing).

- Supply title-generation instructions and the user prompt.
- Permit optional `read`/`bash` exploration, independently of the pruning conversations.
- Default to the configured `small` bucket, with its own specific provider/model override
  in user preferences, including local models. An override affects title generation,
  not the shared bucket or the three pruning settings.
- **Title length decision:** generated titles have a strict maximum of **25 characters,
  including spaces**. Enforce the limit in application validation rather than relying
  solely on prompt wording. This does not approve a suffix exception to the maximum.
- Reuse shared request/tool-turn mechanics and existing guarded recording, current
  permissions, cancellation and owned-tool cleanup. Keep conversation state, output
  validation and metadata publication separate from pruning and main-agent history.

**Pie reference, beyond the selected 25-character maximum:** Pie limits the base title to 25 JavaScript
string characters including spaces, targets 15–20, and permits one corrective request
for invalid output rather than truncating it. Its title generator is request-only;
Urd's optional exploration is an explicit addition. Pie's later trigger, retry and
collision-suffix rules are not imported by describing Urd as similar to Pie.

**Still to settle for titles:** invalid-output/failure handling and interaction with
explicit/manual session names. The precise Unicode counting rule is an implementation
detail to specify consistently in validation and tests. Post-compaction pruning does
not regenerate titles.
The earlier bounded-first-prompt-excerpt fallback remains a proposal, not a new owner
selection. The existing three-row pruning UI decision does not select a title row.

Working recommendation: preserve an explicit/manual name. This remains open for
owner review.

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

**Owner decision:** discover project instruction files the same way as skills:
recursively enumerate matching files through Git, including nested instruction files.
Use the existing Git-tracked-file discovery approach beneath the working directory,
with the instruction-file matching criteria, rather than a separate filesystem crawl.
As with project skills, this scan does not include untracked files or discover project
candidates outside a Git repository. This is a discovery direction, not an implemented
instruction loader.

**Document scope decision:** include agent-facing Markdown instruction resources,
including `AGENTS.md` and append-system resources. Exclude files specific to other
harnesses, such as `CLAUDE.md` and Copilot instructions. This scope is not a request to
inject every Markdown file: ordinary project documentation remains available for
independent exploration, and skills remain their own category. The document scope
does not widen the agreed Git-backed project scan. Exact recognition/loading rules
and non-project resource locations belong in the implementation design; do not
silently reduce this direction to an exhaustive two-filename allowlist.

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

**Structure for owner review, not implementation approval.** Names selected in the
naming table below are settled; other names, signatures, record fields and schema remain
provisional.
Paths are relative to `source/backend/harness/` unless stated otherwise.

The owner accepted the shared discoverer/single typed pruner direction. This is the
proposed concrete structure for that direction, not permission to create these files.

### Naming decisions

| Concept | Owner-selected term |
| --- | --- |
| Rule-bearing content, whether built-in or loaded from files | **Instructions**, rather than guidance or system prompts |
| One identifiable, orderable contribution of text intended for the session's starting context | **`StartingContextContribution`**, replacing the existing `ContextGuidance` name |
| Project-owned result types from shared instruction-resource discovery | **`Discovered` prefix** |
| A loaded instruction document returned by resource discovery | **`DiscoveredInstructionDocument`** |
| A skill's metadata returned by resource discovery | **`DiscoveredSkill`**, replacing the existing `Skill` name |
| The umbrella covering instruction documents and skills | **Instruction resources** |
| The shared operation that discovers and loads instruction resources | **`InstructionResourceDiscovery`** |
| The shared selector for tools, skills and instructions intended for starting context | **`StartingResourcePruner`** |
| A category pruning outcome, carrying retained original items, success/fallback status and diagnostics | **`StartingResourcePruningResult<T>`** |
| The component coordinating discovery, protection, pruning and construction of starting context | **`StartingContextBuilder`**, replacing the existing `StartingContextManager` name |

`StartingContextContribution` defines a contribution, whether its text is fixed or
produced from supplied session data. Preparation may exclude, prune or process it
before composition. Its identity is not tied to becoming one particular system-message
format. This renames the existing abstraction; it does not add a wrapper or another layer.

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

Other concrete names remain under review. Remaining `Guidance` identifiers describe
current code or earlier proposals, not additional settled names. These are design
naming decisions, not permission to refactor source code.

### Files and types

```text
context/
  SkillsGuidance.cs                 change: format supplied skills; no discovery
  runtime/
    StartingContextBuilder.cs       rename/extend StartingContextManager: discover → protect → prune → compose
    StartingContext.cs              change: carry selected resources into composition
    StartingContextContribution.cs  rename existing ContextGuidance abstraction
    InstructionResourceDiscovery.cs new: shared skill/instruction discovery and loading
    DiscoveredInstructionResources.cs new: typed discovered collections; aggregate name proposed
    DiscoveredInstructionDocument.cs new: loaded file-backed contribution
    DiscoveredSkill.cs              rename existing Skill value
    SkillFrontmatter.cs             keep existing parser
  pruning/
    StartingResourcePruner.cs       new: three typed overloads, one selection core
    PruningContext.cs               new: immutable task context and resolved models
    StartingResourcePruningResult.cs new: retained items and outcome/diagnostic
```

`InstructionResourceDiscovery` absorbs discovery/loading from `SkillCatalog`; remove
that old owner once its callers move, rather than retain two discovery pipelines.
Keep kind-specific parsing private to discovery and reuse `SkillFrontmatter`. Preserve
existing packaged-skill and project-override rules. Tools remain registered resources
from `ToolCatalog`, not files found by the discoverer.

The proposed `DiscoveredInstructionResources` aggregate contains
`IReadOnlyList<DiscoveredSkill>` and `IReadOnlyList<DiscoveredInstructionDocument>`.
`DiscoveredInstructionDocument : StartingContextContribution` carries already-loaded
text, source path and scope. It implements the existing ordering/applicability/rendering
contract and requires file data in its constructor, so the parameterless contribution scan
cannot create it accidentally. Neither its constructor nor `GetText` performs file I/O.
This reuses the existing contribution extension point instead of introducing a
second prompt hierarchy or an additional instruction-document/wrapper pair.

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
  choice resolves to the existing `LlmSelection`. Bucket resolution does not exist yet.
  Reuse the same owner for delegated work, not pruning-only buckets. Resolve model choices
  once into `PruningContext`; do not silently inherit the main chat's model.
- `SessionExecution` owns and joins preparation. Extend guarded recording with distinct
  pruning conversation/request scopes and `StartingContextStorage` with selected contexts
  at allowed compaction boundaries. Do not make the builder a second persistence owner.
- Extend `UserPreferences`, `PreferenceManager`, `PreferencesStorage` and their existing
  routes; reuse settings and `ModelPicker`. A small `PruningRows.tsx` under `source/ui/app/`
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

`SkillsGuidance` must format the selected skill list instead of rediscovering everything.
Settings and preparation use `InstructionResourceDiscovery`; the shared registered tool
catalog remains the tool source. If shared execution or bucket resolution lands through another
feature first, reuse it; these are responsibility boundaries, not competing implementations.

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
Candidate payloads remain proposed as skill
name/description/path, existing tool declarations, and full unprotected instruction
text with source/scope. Treat candidate contents as selection material. The model returns
kept candidate IDs, not rewritten resources; the pruner validates/maps them back to the
original typed resources. Invalid output is an ordinary selection failure.

**Selected instruction boundary:** Urd's built-in instructions form fixed base context
and bypass pruning, including the current built-in working defaults/preferences.
Additional discovered instruction documents are candidates unless protected. In the
proposed type layout, built-in `StartingContextContribution` implementations compose
the base and `DiscoveredInstructionDocument` carries document candidates. The skill-list
contribution remains a formatter receiving the selected skills; retaining that formatter
does not retain every skill. The behavior is selected; the concrete API remains proposed.

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

Suggested implementation order, subject to approval: share the existing execution mechanics
without changing main-chat behaviour; add common model resolution and discovery; integrate
selection and per-boundary recording; then preferences and the three status rows. Verification
should exercise protected-input exclusion (including running background tools after
compaction), all three task-input paths and omission of retained continuation context
from selector inputs, parallel isolation, allowed triggers/reopen, per-category failure
and Stop/recording/cleanup failures through existing test helpers.

## Selection failure

**Owner decision (2026-11-15):** if a category's pruner fails or times out, keep all
available entries in that category for the preparation and continue with successful
selections from the other categories. Surface the failure rather than reporting
successful pruning, but do not block the main agent merely because selection failed.
For example, failed tool pruning retains all available tools while successful skill
and instruction selections still apply.

User cancellation stops the preparation; it does not activate keep-all-and-continue.
Existing current-permission and owned-work cleanup requirements still apply. This
settles the fallback, not retry policy or the precise failure UI. The later no-budget
decision rules out adding a pruning-specific timeout; existing timeouts can still fail.

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
- Title-specific behavior listed under [parallel title generation](#parallel-title-generation).
- Concrete classes, APIs, preference/storage shape and implementation sequencing.
  `PruningContext` remains an optional packaging proposal, not a required shared
  investigation or a settled type/name.

**Owner steering (2026-11-15):** duplicate-name/project-override skill protection is
an edge case not worth further design time. Leave its handling to simple implementation;
this does not select name-based versus source-pinned protection.

The [classification evidence](pruning-classification-results.md) informs this choice;
it does not prove the complete three-category design is superior or qualify instruction
pruning with live exploration. Earlier [replay findings](pruning-replay-results.md)
remain dated evidence, not the current plan. No further evaluation or implementation
is authorized by this document alone.
