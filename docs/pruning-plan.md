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

Users choose pruning models through Settings, stored in user preferences, with a
model choice for each category. The implementation must be modular, extendable and
share common execution rather than duplicate pruning loops for the three categories.
Separate conversations do not imply separate engines or new full-session subtypes.

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

## Settings and picker direction

| Category | Model | Protected items |
| --- | --- | --- |
| Tools | Model picker | Core tools plus user selections; Add… |
| Skills | Model picker | User selections; Add… |
| Additional instructions | Model picker | User selections; Add… |

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

Responsibilities discussed, without freezing classes or APIs:

| Responsibility | Owner/direction |
| --- | --- |
| Discovery and source loading | Tool/skill catalogs and instruction loaders |
| Coordination and final composition | Starting-context composition; `StartingContextManager` is the existing seam |
| Selection and result validation | One shared pruning operation with category-specific inputs and rules |
| Model calls, tools, cancellation and usage | Reuse common execution/provider mechanics |
| Preferences and picker | Reuse settings/model-selection components and the same discovery sources |

The proposed shared operation avoids copying execution, failure handling or settings
plumbing when a category is added. It does not require category-specific session
subclasses or a new generic framework. Pruning conversations would not recursively
prune their own starting context.

## Discussed working defaults

These remain implementation proposals, not separately settled contracts:

- Use a small exploration budget; no numeric limit has been selected.
- Return kept candidate IDs rather than rewritten instruction documents; validate and
  combine them with protected entries in stable source/precedence order.
- Retain a category if its selector fails or times out, rather than silently dropping
  its entries. This fallback is not a response to user cancellation.
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
- Exact instruction discovery, stored source identities and source-loading behavior
  (distinct from the settled pruning triggers), exploration limits and execution restrictions.
- Concrete classes, APIs, preference/storage shape and implementation sequencing.

The [classification evidence](pruning-classification-results.md) informs this choice;
it does not prove the complete three-category design is superior or qualify instruction
pruning with live exploration. Earlier [replay findings](pruning-replay-results.md)
remain dated evidence, not the current plan. No further evaluation or implementation
is authorized by this document alone.
