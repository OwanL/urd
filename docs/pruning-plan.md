# Starting-context pruning plan

**2026-11-15 — active design plan, not implementation approval.**

This is the canonical record of the pruning direction discussed so far. Owner-selected
requirements are separated from working proposals and unresolved details. It does not
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
`write` and `edit`** as core tools. Users can add protected tools, skills and instruction
documents.

Protected entries bypass pruning and remain available to the main session. Exclude
them before constructing pruning inputs, not merely by restoring them after selection:

- Do not send their candidate entries or a separate list of protected IDs.
- Do not reintroduce protected documents through an unfiltered shared-guidance input.
- Preserve protections when applying selection results too.

The pruner's exploration tools are a separate surface: it can still see and invoke
`read` and `bash`, although those tools are not candidates it may prune. Protection
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
instruction loader or a decision about additional non-project instruction sources.

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

All three selectors work from the same task and grounding snapshot, excluding
protected resources from their supplied selection context. Tools and skills do not
wait for the instruction selector's output. Each has its own category-specific brief.

## High-level structure proposal

**For owner review, not an approved class layout or implementation plan.** Names below
make the responsibilities concrete; signatures, record fields and schema are not frozen.
Paths are relative to `source/backend/harness/` unless stated otherwise.

| Proposed owner/file | Change and responsibility |
| --- | --- |
| `context/runtime/StartingContextManager.cs` | Extend the existing composer with asynchronous preparation: collect candidates, separate protections, run category selection in parallel and compose the final context. It does not own session admission or durable writes. |
| `context/runtime/GitFiles.cs` | Extract the Git listing mechanics currently private to `SkillCatalog`; reuse a listing for matching skills and instruction files. Not a new filesystem discovery framework. |
| `context/runtime/InstructionCatalog.cs` and `InstructionDocument.cs` | Discover/load recognised instruction documents with their source and scope. Keep existing `SkillCatalog` and `ToolCatalog`; do not merge their distinct semantics into one generic catalog. |
| `context/pruning/CatalogPruner.cs` | One category-independent selection operation: build its brief, run the bounded conversation, validate kept IDs and return a selection or ordinary failure. Three invocations, not three implementations. |
| `context/pruning/PruningPrompt.cs` and `Pruning.cs` | Category-specific prompt construction plus small input/result records. No skill/tool/instruction pruner subclasses or strategy/plugin hierarchy. |
| `execution/ConversationRunner.cs` | Extract the shared streamed-response validation and tool-turn mechanics from the current execution path. Main execution and pruning use the same implementation; primary admission, steering, compaction and settlement stay outside it. Continue using `IChatClient`, existing adapters and tool dispatch rather than adding a model-client facade. |
| `providers/ModelSelector.cs` | Shared model-choice resolution: configured bucket or explicit provider/model reference becomes the existing `LlmSelection`. Bucket resolution is not implemented today; this is a dependency, not something pruning can already call. Reuse the same owner for delegated work rather than create pruning-only buckets. |
| Existing execution/recording/storage owners | `SessionExecution` owns the preparation lifetime and joins its branches. Extend guarded recording with distinct pruning conversation/request scopes; extend `StartingContextStorage` for selected contexts at allowed compaction boundaries. Do not make the composer a second persistence owner. |
| Existing preferences/server/UI owners | Extend `UserPreferences`, `PreferenceManager`, `PreferencesStorage` and existing routes; reuse settings and `ModelPicker`. Add a small pruning-row renderer under `source/ui/app/`, driven by recorded category status rather than owning work. Detailed UI remains deferred. |

`SkillsGuidance` must format the selected skill list instead of rediscovering the full
catalog during composition. Settings and preparation use the same catalog sources.
If shared execution or bucket resolution lands through another feature first, reuse it;
these are responsibility boundaries, not competing implementations.

### Proposed data flow

1. The owning execution checks whether the current context boundary already has a saved
   selection. Use the session's initial boundary or the latest successful compaction ID,
   not every history change, model change or preference change.
2. At an allowed preparation boundary, capture the task context, discovery results,
   preferences and resolved models once. Separate protected entries before building
   selector inputs. Each selector has its own conversation and category brief.
3. Call the same `CatalogPruner` for the three categories in parallel. Each may use the
   shared tool-capable execution path for exploration, but never recursively prune itself.
4. Validate returned IDs against that category's candidate set. Keep original resources
   in stable source/precedence order; the model does not rewrite documents or schemas.
   Apply ordinary per-category keep-all fallback and combine with protected entries.
5. Once all owned pruning work has settled safely, the execution records/uses the new
   context at the main-request boundary. Ordinary requests and reopen reuse the saved
   selection. Compaction alone does not launch selectors.

**Proposed input contract:** a fresh session supplies its admitted prompt; after
compaction, supply the summary, retained recent context and any next admitted input,
not the discarded history. Do not copy the main system-guidance bundle into these
inputs and thereby reintroduce protected documents. Candidate payloads are skill
name/description/path, existing tool declarations, and full unprotected instruction
text with source/scope. Treat candidate contents as selection material. Output is kept
candidate IDs, not rewritten resources; invalid output is an ordinary selection failure.

**Proposed instruction boundary:** compiled `ContextGuidance` blocks form fixed base
guidance; discovered additional instruction documents are pruning candidates. The skill
list block remains a formatter, but receives the selected skills. This is a proposed
boundary for review, not a newly selected protection rule.

### Integration constraints the design must address

- **Not three full sessions.** The rows represent owned pruning conversations, not new
  `SessionBase` subtypes, independent chats or another auxiliary-job framework. The
  earlier request-only pruning sketch predates the selected `read`/`bash` exploration:
  titles and compaction can remain request-only; tool-capable pruning needs shared turn
  execution as well as `IChatClient`.
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
- **Fallback is not a catch-all.** Provider/selection failures or a cleanly terminated budget
  expiry retain that category. Parent Stop cancels and joins all branches. Uncertain tool
  effects, failed durable recording or failed cleanup keep the existing execution fence;
  they must not be converted into keep-all-and-continue. A timeout requests cancellation,
  not proof that tools have stopped. Exact exploration limits are still to choose.

Suggested implementation order, subject to approval: share the existing execution mechanics
without changing main-chat behaviour; add common model resolution and discovery; integrate
selection and per-boundary recording; then preferences and the three status rows. Verification
should exercise protected-input exclusion, parallel isolation, allowed triggers/reopen,
per-category failure and Stop/recording/cleanup failures through existing test helpers.

## Selection failure

**Owner decision (2026-11-15):** if a category's pruner fails or times out, keep all
available entries in that category for the preparation and continue with successful
selections from the other categories. Surface the failure rather than reporting
successful pruning, but do not block the main agent merely because selection failed.
For example, failed tool pruning retains all available tools while successful skill
and instruction selections still apply.

User cancellation stops the preparation; it does not activate keep-all-and-continue.
Existing current-permission and owned-work cleanup requirements still apply. This
settles the fallback, not retry policy, timeout values or the precise failure UI.

## Discussed working defaults

These remain implementation proposals, not separately settled contracts:

- Use a small exploration budget; no numeric limit has been selected.
- Return kept candidate IDs rather than rewritten instruction documents; validate and
  combine them with protected entries in stable source/precedence order.
- Capture model choices when preparation begins; settings changes affect subsequent
  preparations rather than changing an in-flight run.

Existing Stop ownership still applies: cancel and join the owned pruning conversations
and their tools, without starting the main agent after cancellation. Preserve existing
prefix-stability, current-permission and capability-recovery boundaries; pruning does
not replace those mechanisms.

## Still unresolved

- Whether core protections are non-removable pins or editable defaults. Protecting the
  four named tools is selected; the settings lock/removal behavior is not.
- The exact boundary between the base operating prompt and additional instruction
  candidates, and any automatically protected instruction layers. This plan does not
  silently protect every applicable `AGENTS.md` or settle all instruction precedence.
- Remaining instruction matching criteria/non-project sources, stored source identities
  and source-loading behavior (distinct from the settled discovery approach and pruning
  triggers), exploration limits and execution restrictions.
- Concrete classes, APIs, preference/storage shape and implementation sequencing.

**Owner steering (2026-11-15):** duplicate-name/project-override skill protection is
an edge case not worth further design time. Leave its handling to simple implementation;
this does not select name-based versus source-pinned protection.

The [classification evidence](pruning-classification-results.md) informs this choice;
it does not prove the complete three-category design is superior or qualify instruction
pruning with live exploration. Earlier [replay findings](pruning-replay-results.md)
remain dated evidence, not the current plan. No further evaluation or implementation
is authorized by this document alone.
