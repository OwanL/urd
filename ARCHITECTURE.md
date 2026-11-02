# Urd architecture and feature decisions

## Status and scope

This is a living design record, not an implemented architecture. The project
purpose and strategic constraints are owned by [README.md](README.md).

The review includes feature value as well as interfaces, data models,
dependencies, class/module structure, ownership, coupling, failure behaviour
and performance. Pie provides the starting inventory; it is not a mandatory
parity checklist. Retaining, simplifying, replacing and removing features are
all valid outcomes when explicitly justified.

Only decisions recorded as agreed are settled. Review topics, observations
about Pie and recommendations do not select an implementation by themselves.

## Review method

For each area, establish:

1. **Value:** the user or agent outcome, evidence of use and the cost of losing
   it. Distinguish a useful capability from an unnecessarily complex mechanism.
2. **Model and ownership:** entities, identities, relationships and the single
   authority for each kind of state; distinguish durable state from live work
   and client presentation.
3. **Interfaces:** commands, queries, events and dependency direction, including
   who may initiate effects and which guarantees cross a boundary.
4. **Lifecycle and failure:** admission, concurrency, cancellation, partial
   failure, restart and recovery. Do not equate a lost acknowledgement with
   proof that an operation did not happen.
5. **Cost:** expected scale, latency, memory, I/O and maintenance burden. Mark
   unmeasured performance assumptions rather than presenting them as facts.
6. **Decision and evidence:** retain, simplify, replace, remove or defer;
   rationale, consequences and the behaviour that will verify the choice.

Resolve responsibilities and behaviour before choosing class hierarchies or
frameworks. A class, worker process or interface is a possible implementation
mechanism, not a feature in itself. Do not create abstraction layers merely to
mirror this review structure.

## Review coverage

These topics are pending review, not a committed subsystem layout. Each may
span multiple decisions, and the inventory can grow as Pie is inspected.

| Area | Questions to resolve |
| --- | --- |
| Client and backend authority | Shared session state versus independent navigation, drafts, settings and concurrent client actions. |
| Sessions and conversation history | Session identity, branches/forks, turns, messages, attachments and model context versus displayed history. |
| Storage and recovery | Durable truth, transactions, indexes, large payloads, retention, restart and interrupted work. |
| Agent execution | Agent loop, admission, streaming, tool dispatch, cancellation, isolation and resource ownership. |
| Providers and models | Provider adapters, credentials, model metadata/selection, request limits, retries, fallback, usage and pricing. |
| Guidance and resources | Bundled guidance, user/project context, named agents, skills, discovery and precedence. |
| Tools and integrations | Tool contracts, validation, result representation, side effects, shell/browser/computer tools and MCP. |
| Context management | Tool/skill selection and recovery, tool-result reduction/recall, history compaction and their distinct purposes. |
| Delegation and coordination | Bounded subagents, independently running sessions, assignments, messages, results, waits and shared-resource coordination. |
| Deferred work | Timers, wakeups, event triggers, cancellation and restart expectations. |
| UI and transport | Shared frontend, host integrations, views, commands, streaming, synchronization, paging and reconnection. |
| Analytics and configuration | Accounting, observability, privacy, user settings and separation from execution-critical state. |
| Lifecycle and delivery | Backend startup/discovery, background controls, updates, source customisation, development builds and packaging. |
| Access and trust | Local/remote access, credentials and permission boundaries without assuming multi-user hosting or sandbox isolation. |
| Verification and performance | Behavioural acceptance, representative workloads, failure testing and dependency/complexity costs. |

## Agreed architectural decisions

The existing strategic decisions remain in the README: a first-party harness
without Pi; a shared backend with multiple UI clients; background operation
after UI closure; source-based customisation; Windows-first delivery; and no
Pie data-compatibility requirement. More detailed decisions will be recorded
here as the review progresses. Keeping LOC down is also a strict project
requirement; see the README's code-size constraint.

### Independent client navigation: conditional on minimal cost

**Decision:** Independent client navigation is worthwhile if it adds no more
than **100 implementation LOC** over a simple mirrored-selection implementation.
It is not important enough to justify substantial extra machinery.

**Scenario:** VS Code displays session A while a browser displays session B,
allowing multiple sessions to remain visible at once. Both clients still use
the same underlying backend and sessions.

**Rationale:** The benefit is simultaneous visibility, not collaborative editing
or a second execution backend. Pie currently mirrors selection across its
renderers; Urd need not inherit that choice if independence is cheap.

**Status:** The cost condition has not been demonstrated. Do not present
independent navigation as unconditional scope or invent a cost estimate. When
implementing, compare the minimal designs without building a configurable
framework simply to support both. Draft ownership is a separate, unresolved
question.

### Preserve bucket-based model selection for agent-created work

**Decision:** Retain Pie's bucket system for agents creating other agents.
The selected bucket expresses the task's complexity; configured model choices
supply the actual provider/model. This is a model-selection policy, separate
from how sessions are represented or stored internally.

The existing `small`, `medium` and `frontier` buckets remain the reference
behaviour. **Keep bucket-only creation for now; do not add an exact-model
creation override.** In current Pie, both subagent creation and independent
session creation require a bucket. An independent session's model can be
changed afterwards through its settings; capability requirements such as image
support are distinct from choosing an exact model.

**Rationale:** Preserve the current selection workflow rather than expand its
interface during the rebuild. Internal session representation remains a separate
design question. Provider eligibility, fallback and selection internals still
need their own review; retaining buckets does not approve every current
implementation detail.

### Retain bounded subagents and independent agent-created sessions

**Decision:** Both workflows earn their place in Urd; neither is being removed
as redundant.

- **Bounded subagent:** a focused delegated task with a direct call/result
  relationship to its caller and its own model context.
- **Independent agent-created session:** an ongoing conversation that can receive
  follow-up assignments, coordinate with other sessions and be steered by the
  user, rather than only returning one result from a helper call.

**Rationale:** They serve different work patterns: focused assistance versus
sustained parallel work. Both use the retained bucket-based creation policy.
This does not require separate agent engines, storage implementations or class
hierarchies. Sharing their underlying representation remains a proposal, and
exact cancellation, retention and ownership rules still need review.

## Working hypotheses, not settled commitments

### Begin with one backend process unless evidence justifies separation

A single process for the server and agent execution is the current working
default, **not a locked architecture decision**. It could remove routine
coordinator/worker IPC and cross-process ownership machinery. Ordinary
provider/tool failures must still be contained to their operation; a fatal
process failure would interrupt all active sessions.

Revisit the boundary when actual isolation, blocking work or performance needs
justify it. External commands and selected integrations can still run in their
own processes. Keeping the option open is not a reason to implement both
execution models or add speculative process-abstraction layers now.

### Shared representation for ordinary and delegated sessions

Using a common session representation for conversation, settings and execution,
with explicit delegation relationships and lifetime rules, is a proposal only.
It has not been agreed. This question concerns internal data structures, not
which LLM serves a task; bucket-based model selection does not answer it.

Pie already uses its agent engine for both forms. A Urd simplification must
therefore target real lifecycle, persistence or inspection duplication rather
than assume Pie has two unrelated engines. Visible tabs, inherited context,
child permissions and lifetime remain separate concerns.

## Reference observations

Initial inspection used Pie commit
`cc82abb0f317f31341d3a6f12aa1545f6be1fbea`; this identifies evidence, not a frozen
parity target. Relevant paths are in the **Pie repository**, not Urd:

- `docs/architecture/ARCHITECTURE.md`: current runtime/process responsibilities,
  state flow, dependency ownership, storage and delivery.
- `docs/contracts/STATE_CONTRACT.md`: client/session authority, synchronization,
  model selection and recovery guarantees.
- `docs/contracts/AGENT-SESSION-CONTROL.md`: session coordination semantics.
- `harness/agent-instructions/README.md` and `harness/tools/README.md`: guidance
  and tool responsibilities.
- `harness/tool-and-skill-selection/README.md` and
  `docs/contracts/TOOL-RESULT-PRUNING.md`: distinct context-management mechanisms.

Plans and implementation details require checking against current source and
qualification status. A Pie implementation constraint is not automatically an
Urd requirement.
