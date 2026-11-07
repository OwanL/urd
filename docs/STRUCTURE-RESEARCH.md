# Structure research and alternatives

2026-11-05. **Research behind the current rough structure.** The owner rejected the
old `systems/` proposal (commit `1f0eda5`): “Systems is far too vague. Do some actual
research and brainstorming.” This document records that investigation and the later
naming/grouping refinements. The [application plan](APPLICATION-PLAN.md) now owns the
accepted rough baseline, explicitly open to correction during implementation.

Broader harness nesting, consistent guidance terminology and concrete session areas
replace the rejected grouping. Less-developed application areas remain TBD. The
responsibility map below is not a folder/project per row or a frozen class design;
research evidence and rough-layout acceptance do not authorize production code.

## 1. What was wrong with the proposal

Removing the `systems/` directory would not be enough:

- `capabilities` bundled executable tools, MCP connection lifetime, model-visible
  declarations and relevance selection. These do not have one state owner or lifetime.
- `resources` did not explain whether it meant instructions, skills, attachments,
  MCP resources or machine capacity. These are different concepts.
- `coordination` hid assignments/results, future triggers and live resource claims
  behind one label. Sharing session IDs does not make their recovery rules the same.
- `accounting` absorbed diagnostics; `configuration` threatened to absorb every
  feature's policy. Shared logging/settings I/O is not shared business ownership.
- The bespoke Core project inclusion scheme optimized a directory drawing before
  establishing whether its grouping helped someone understand or change Urd.

The next design should answer **what changes here, what state belongs here, who can
change it, and what must remain unaffected**. Trace execution to test a boundary;
do not assume every pipeline stage deserves a module. No module/interface per class,
mandatory service/repository layering or new framework follows from this criterion.

## 2. Primary-source investigation

Bounded read-only source inspection, not execution or product qualification. Public
source was fetched, not installed or adopted. Pie was inspected at local checkout
`cc82abb0f317f31341d3a6f12aa1545f6be1fbea`; no personal configuration/data was read.
OpenCode and Codex source observations below refer to the pinned revisions in links,
not whichever code later occupies their default branches.

### Pie: closest product reference

| Inspected source and symbols (paths relative to Pie) | Evidence | Consequence for Urd |
| --- | --- | --- |
| `harness/model-providers/catalog/provider-model-resolution.ts`: `resolveProviderModelList`; `management/auth-port.ts`: `listSavedCredentialIds`, `getRuntimeApiKey` | Offline model metadata resolution is explicitly separate from authenticated access. The auth port distinguishes presence from credential resolution and credential epochs. | Within the provider domain, separate catalog/model selection, account access and request construction. A single vague `ProviderManagement` class is not a sufficient class design. |
| `harness/tools/catalog/index.ts`: `PIE_TOOLS`, `pieToolsForContext` | Metadata-only inventory; implementation owns schema/guidance. Context eligibility is not active visibility. Listing does not initialize browser/native tool runtimes. | Tool definitions and invocation belong to tools, but inventories should not start costly execution resources. |
| `harness/tools/request-capability/index.ts`: `createRequestCapabilityTool`, `RequestCapabilityPorts` | Stateless entry point receives selection state through explicit ports. Current tool recovery calls an installed `invoke_tool` controller rather than widening the frozen native catalog. | Recovery is a tool entry point into context/exposure ownership, not a second selection store. Do not copy Pi discovery adapters or create a generic capability framework. |
| `harness/agent-instructions/agent-discovery/agents.ts`: `discoverAgents` | Scoped definition discovery resolves bundled/user/project candidates and whole named-agent replacements. It does not create a live agent. | Authored agent definitions are instruction/profile data, not session subclasses. Exact Urd loading behavior remains deferred. |
| `harness/agent-processes/coordinator/session-tasks.ts`: `SessionTaskRegistry.assign`, `amend`, `started`, `settled` | Explicit requester/worker/revision/result state; registry expressly has coordinator lifetime, no disk persistence/replay/scheduling. | Keep messages/assignments distinct from deferred triggers. Preserve useful concepts, not Pie's volatile storage mechanics: Urd's selected durable facts differ. |

The tools/selection READMEs were useful navigation, not sufficient behavioral evidence:
the selector README's recovery description still discusses native activation, whereas
the inspected recovery implementation explicitly uses the append-only wrapper route.
Urd's [own selected exposure rules](DISCOVERY-DESIGN.md) govern, not that README.

### OpenCode: catalog, account, request and tool boundaries

At `8bae6b5c1c6d81effde6b10670d8b10ebe400ff8`, relevant code is under
`packages/core/src`, not the older `packages/opencode/src` tree found in search results.

- [Model resolution](https://github.com/anomalyco/opencode/blob/8bae6b5c1c6d81effde6b10670d8b10ebe400ff8/packages/core/src/model-resolver.ts#L442-L535)
  keeps catalog identity distinct from API model ID and resolves the selected connection.
  [Provider snapshots](https://github.com/anomalyco/opencode/blob/8bae6b5c1c6d81effde6b10670d8b10ebe400ff8/packages/core/src/provider.ts#L477-L546)
  check connection presence without token resolution and track account-specific model observations.
- [Credentials](https://github.com/anomalyco/opencode/blob/8bae6b5c1c6d81effde6b10670d8b10ebe400ff8/packages/core/src/credential.ts#L72-L145)
  and [connection resolution](https://github.com/anomalyco/opencode/blob/8bae6b5c1c6d81effde6b10670d8b10ebe400ff8/packages/core/src/integration.ts#L694-L734)
  coordinate shared refresh independently of location/session cancellation. A selected
  model, an available account and a prepared request are not the same object.
- [MCP tool integration](https://github.com/anomalyco/opencode/blob/8bae6b5c1c6d81effde6b10670d8b10ebe400ff8/packages/core/src/tool/mcp.ts#L37-L153)
  gets registrations from the MCP owner, wraps execution with permission checks, and
  reconciles tool-change notifications. It does not make the tool registry own MCP itself.
  [Tool snapshots/execution](https://github.com/anomalyco/opencode/blob/8bae6b5c1c6d81effde6b10670d8b10ebe400ff8/packages/core/src/tool.ts#L247-L327)
  distinguish registrations, advertised definitions and invocation; Code Mode makes
  their exposed inventory different again. Do not copy that execution strategy into Urd.
- [Skill preparation](https://github.com/anomalyco/opencode/blob/8bae6b5c1c6d81effde6b10670d8b10ebe400ff8/packages/core/src/skill.ts#L33-L97)
  separates available skill records from loaded instructional content. The complete
  prompt-injection path was not traced.

**Useful lesson:** related objects can have different scopes: durable account,
location catalog, request snapshot and individual call. Neither a provider directory
nor a universal service bag communicates those lifetimes by itself. This inspection
stopped at `LLMClient.generate`/`Mcp.callTool`; low-level transport and teardown were
not qualified. OpenCode's Effect layers and extensibility are not Urd requirements.

### Codex: live handles, runs and cooperation—not an “agent” hierarchy

At `e8e3197e84c650eea674321da2a434ee8851da28`:

- [ThreadManagerState](https://github.com/openai/codex/blob/e8e3197e84c650eea674321da2a434ee8851da28/codex-rs/core/src/thread_manager.rs#L518-L547)
  holds live thread handles plus considerable shared infrastructure.
  [CodexThread](https://github.com/openai/codex/blob/e8e3197e84c650eea674321da2a434ee8851da28/codex-rs/core/src/codex_thread.rs#L196-L208)
  is a command-facing handle, while [Session](https://github.com/openai/codex/blob/e8e3197e84c650eea674321da2a434ee8851da28/codex-rs/core/src/session/session.rs#L63-L112)
  owns per-thread state/input/active-turn state. [RunningTask](https://github.com/openai/codex/blob/e8e3197e84c650eea674321da2a434ee8851da28/codex-rs/core/src/tasks/mod.rs#L176-L224)
  has execution/cancellation/completion resources. This supports Urd's selected
  session-versus-execution distinction, not a new subtype per workflow.
- [LocalAgentControl](https://github.com/openai/codex/blob/e8e3197e84c650eea674321da2a434ee8851da28/codex-rs/core/src/agent/control.rs#L93-L129)
  carries controller identity and tree-level shared runtime, using ordinary thread
  admission for input. [Collaboration tool handlers](https://github.com/openai/codex/blob/e8e3197e84c650eea674321da2a434ee8851da28/codex-rs/core/src/tools/handlers/multi_agents.rs#L1-L6)
  call that owner rather than becoming the owner. History lineage and control parent
  are separate concepts; Codex's group-level `SessionId` is not Urd's individual session ID.

**Counterexamples matter:** `ThreadManagerState` aggregates many dependencies and tool
routing can receive the whole session. Do not turn that into a Urd manager/service bag.
Codex's bounded graceful-abort window is not proof of Urd's actual-effect settlement;
status-derived parent notification is not Urd's explicit durable assignment result.
Storage transactions, concrete dispatch permission enforcement and full tree shutdown
were outside the seven-file investigation. Its folder names do not certify good boundaries.

## 3. Three genuinely different organization choices

| Candidate | Main unit of organization | Strength | Cost/problem for Urd |
| --- | --- | --- | --- |
| **A. Explicit domain modules** | Providers, guidance, tools, model context, session messages/assignments, wakeups, history, etc.; host/UI remain distinct. | A maintainer can locate the owner of a behavior. Shared state/rules have one place. Can remain one backend library. | More visible peers. Some changes necessarily cross domains; an input feature can touch UI, endpoint and session command. Must not replace `systems` with another unexplained catch-all. |
| **B. End-to-end feature slices** | “Connect provider”, “send message”, “delegate work” groups each workflow's changes across its UI/API/domain/data owners. | Very good locality for adding a user-facing workflow; avoids ceremonial controller→service→repository layers. Can respect the existing language/project boundaries. | Urd's UI and agent tools invoke the same long-lived state machine. Per-request slices must still share that authority, not duplicate admission/state policy in each handler. Useful within domains, not my whole-product default. |
| **C. Reusable harness library plus workspace application** | General execution/tool/provider/guidance package; app owns workspace/history/cooperation/clients. | Strong reuse boundary; straightforward headless embedding and separate consumers. | Urd is one specific product, not an arbitrary harness framework. Designing public extension/lifecycle contracts now risks recreating Pi's seams and generic Core bucket. Extra machinery lacks a demonstrated consumer. |

B is a real alternative, not a straw man: [Bogard's original vertical-slice explanation](https://www.jimmybogard.com/vertical-slice-architecture/)
groups changes across layers and minimizes inter-slice coupling. Its lesson about
avoiding mandatory abstractions transfers; Urd need not adopt the entire request-centric
shape. C could earn its cost with an actual independent embedding requirement later.

**Working preference: A, using B's change-locality discipline inside a domain.**
This was the research recommendation; subsequent rough-layout alignment is recorded
in the [application plan](APPLICATION-PLAN.md), not a frozen module/class contract.
The existing Core/Server compile boundary survives all three comparisons; neither
A nor the word “module” implies another assembly, process or plugin boundary.
A clarifies state ownership; it has **not demonstrated fewer files, fewer changed
locations or fewer lines than B**. Those costs need real implementation evidence.

## 4. Candidate domain map—state and exclusions, not another folder tree

| Concrete owner | State/decision it hides | Deliberately outside it |
| --- | --- | --- |
| **Sessions and execution** | Live session identity/admission and current execution; pending input, cancellation and settlement. Preserve the two session kinds. | Account management, HTTP and a global container of all collaborators. |
| **Model providers** | Model/catalog identities, accounts/credential access, provider adapters and request policy. Distinguish catalog, account and request collaborators internally. | Session input policy; caller-specific title/compaction/pruning behavior. |
| **Guidance loading** | Scoped prompt/skill/named-agent discovery and reading, definition provenance. | Authored content itself; attachments; runtime agents; what a particular model has already seen. |
| **Tools** | Concrete tool definitions/implementations, registration and one current-authority invocation path. Simple tools implement their operation directly; application tools enter their domain's commands. | Owning the session/assignment behind a tool; model-context history; starting native runtimes just to list metadata. |
| **MCP connections** | Server configuration/connection lifetime, SDK protocol calls and discovery changes; exports tool registrations to Tools. | Model relevance selection or an agent loop. Separate logical owner; may be a small area beside tool integration rather than another top-level project. |
| **Model context** | The per-session working representation, selected/loaded exposure, compaction and reduction/recall orchestration. Session guards govern applying changes; provider adapters own native representations. | Original transcript authority or tool permission. Tools/instructions own eligibility and definitions; context consumes them rather than building duplicate source catalogs. |
| **Inter-session messaging and assignments** | Messages, assignments, explicit results, waits and any retained cooperative-resource support, with distinct lifetimes. | Live-session registry ownership; future trigger dispatch; inferring a result from idle status. |
| **Session wakeups** | Recorded trigger/target/delivery state and dispatch into ordinary session admission. | A general replay/workflow engine or a second way to start a busy worker. |
| **Session history & storage** | Original history, durable context/session/work records, bounded queries, attachments and atomic intent-specific writes. | Current execution state, automatic replay, or deleting originals because context was compacted. Keep the selected `SessionStore` authority; do not add a wrapper per table. |
| **Usage** | Observed request usage, price provenance/estimates and retained summaries. | Agent control, full conversation retention or a new generic telemetry platform. |
| **User interface** | Views, drafts/navigation, provider/settings screens, pending questions and bounded caches. | Lifetime of a pending agent question or accepted backend work. |
| **Hosting** | Process lifetime, composition, access/transport and platform entry. | Provider/session policy or source-build orchestration. |
| **Development & delivery** | Declared tools, source/build/data separation, checks and the customization/distribution workflow. | Runtime session state or an automatic update platform. |

Central editable Markdown remains a separate authored-content home. “Guidance loading”
above describes code, not a second content directory. Loading/reload timing remains deferred.

Questions/answers should start as an **execution-owned interaction** with UI presentation,
not an unexplained global `interactions` system. If shared correlation/routing needs a helper,
give it that precise job; closing a browser does not cancel the originating execution.
Provider-login prompts remain owned by the login operation, not by an agent run.

Configuration has shared read/write mechanics, but each domain owns its settings' meaning
and validation. Standard host logging handles ordinary diagnostics; Usage owns retained
accounting. Neither warrants a global policy manager or a fresh subsystem by default.

## 5. Change probes against candidate A

These are paper design checks, **not tests that have run**. Their value is exposing
cross-domain work rather than claiming every change fits inside one folder.

| Change | Expected owners | Boundary failure to avoid |
| --- | --- | --- |
| Add a provider/auth route | Providers; generic provider UI/host surfaces only where new interaction requires it; Context for genuinely new native representation qualification. | Editing the agent loop for every provider, or refreshing credentials merely to list model metadata. |
| Add a file tool | Tools implementation plus explicit registration; its schema/guidance stay with it. | Another service class just to wrap file I/O, or changes to execution/context/HTTP plumbing for an ordinary tool. |
| Disconnect or incompatibly redefine an MCP tool | MCP updates availability/registrations; Tools checks current authority and compatible identity. Context retains historical disclosure. | Rewriting past definitions/history or silently rebinding an old exposed definition to an incompatible implementation. Exact compatibility checks still need qualification. |
| Change a prompt or skill | Authored file; the guidance loader reads at the selected loading boundary; Context controls future assembly/append. | Rebuilding provider code, retroactively replacing a loaded skill result or quietly introducing a watcher. |
| Change selection/compaction | Context policy plus relevant tool/instruction eligibility; provider-specific encodings remain in adapters. | A second permission catalog, compaction deleting originals, or an auxiliary “session” per model call. |
| Accept an assignment / fire a wakeup | Assignment or wakeup handling enters the same execution admission path and SessionStore transaction as ordinary input. | Independent busy checks/starts, or splitting the correlated durable acceptance into separately committed module writes. |
| Close a chat but retain selected totals | Session close policy, History/Storage cleanup and Usage retention. | Hard-coded “delete all rows with session ID” or retaining the full transcript solely for billing. |

Shared transactions are allowed. Module boundaries must not force asynchronous messages
or separate databases between admission and durable assignment/input records. Coordination
constructs the intent; the existing guarded command and intent-specific store operation
remain the mutation path. The same rule applies to context/exposure/result publication:
no independently writable selection store may race session closure or editing.

## 6. Small class/state-owner sketches for candidate A

Working names below illustrate the cut; only the existing session alignment is selected.

```text
ModelCatalog            metadata, model identities and selection inputs
ProviderAccounts        credentials/login and account-scoped access/refresh
Provider adapters       construct configured request-only IChatClient;
                        own protocol/native-context conversion
```

Listing models reads catalog/account-presence projections, not token-refresh operations.
Executing a request resolves the selected model/account through the adapter. Titles,
compaction and ordinary replies use the same request interface, with separate request
state and caller-specific failure policy. Shared refresh, if needed, is not owned by
whichever session happens to request it first. This does not select a credential store
or import OpenCode's cross-process locking framework.

```text
SessionBase owns current ContextState + 0..1 SessionExecution
  ContextBuilder / CatalogPruner read GuidanceCatalog + ToolCatalog
  Context changes/results publish through the session guard + SessionStore

SessionExecution uses IChatClient and ToolExecutor
  ToolExecutor checks current Tools authority -> concrete tool
  MCP-backed tool -> MCP connection owner
  Recovery tool  -> guarded context/exposure operation
```

`GuidanceCatalog` owns available scoped definitions; `ToolCatalog` owns current
registrations; `ContextState` records what this session has selected/loaded/sent.
These are not three interchangeable mutable inventories. Catalog selection cannot
grant tool permission, and recovery cannot mutate a second independent context store.
The engine does not import each concrete tool implementation. Question-tool waits
belong to the execution; the host/UI route answers to that identified wait.

```text
Session tool or UI command -> AssignmentCoordinator -> PrimarySession admission
Trigger fires             -> WakeupDispatcher      -> PrimarySession admission
                                                      -> atomic SessionStore intent
```

These share the session guard and store authority, not identical admission policies:
an assignment can be rejected as busy while ordinary mid-response input remains
supported. Coordinator/dispatcher do not acquire a parallel execution slot or write
worker input separately. The session registry still owns live-handle lookup, not
assignment/result meaning. No new base classes or command-bus framework are implied.

## 7. What this changes—and what it does not settle

This is more than flattening `systems/`: move model-facing selection to Context; keep
registration/execution with Tools; make MCP connection lifetime explicit; separate
trigger dispatch from messaging/assignments; name guidance discovery precisely; keep
usage distinct from generic diagnostics; distribute policy ownership instead of
centralizing it in Configuration. Existing selected behavior is not being redesigned.

Logical owners need not be peer folders or separate projects. The later owner feedback
below resolves the direction for harness grouping, not all remaining placement/naming.
The common database is not a reason to merge all domains; nor is domain separation a
reason to invent many repositories or assemblies. This research does not authorize
production scaffolding, new provider/account calls or activation.

## 8. Naming and harness grouping

**Owner direction:** broader harness nesting is accepted as sensible. The proposed
`agent-content/` name is rejected as misleading; the owner prefers “guidance”,
“instructions” or similar, with consistent terminology for the loader code. The labels
“collaboration” and “follow-ups” also fail to explain their purpose. The owner expects
usage accounting to sit within the harness, considers either placement of session
storage arguable, and warns against mistaking harness detail for a complete application
design. Less-developed areas may be marked TBD. The owner subsequently accepted the
revised rough structure as a decent starting point, explicitly open to correction
during implementation, and requested centralized repository directories such as docs.
The current [working baseline](APPLICATION-PLAN.md) records that alignment; production
implementation still needs an explicit scoped task.

**Revised proposal:** use `harness` for the broader agent machinery and `execution`
for the narrow model/tool loop. Keep named responsibilities inside that grouping;
do not turn the grouping into a single Harness manager or execution-owned service bag.
Provider accounts and MCP connections retain their appropriate shared lifetimes.

Use **`guidance/`** for the editable collection and **`guidance-loading/`** for code that
discovers, resolves and reads it. An equivalent consistent pair is `instructions/`
and `instruction-loading/`; the working layout uses guidance consistently. The authored
home holds bundled defaults, not just overrides, and does not absorb existing user/project
instruction files or select loading/reload timing.

Replace “collaboration” with its concrete responsibilities: **session messaging**
(notes/input between sessions) and **assignments** (requester/worker, delegated work,
explicit result and waits). Replace “follow-ups” with **session wakeups** (timer,
event or condition triggers delivering recorded input to a target session). These
are subareas of `sessions/`, not new services or separate commit paths. Any retained
cooperative-resource support remains an explicit open placement concern; this naming
change does not silently remove it or turn assignments into a general resource manager.

The canonical [working layout](APPLICATION-PLAN.md#working-layout) now lives in the
application plan, including centralized documentation and repository-support areas.
Keep the rationale here rather than maintaining a second diverging tree.

**Storage recommendation:** session transcripts, model-context records, assignment
facts and managed attachments have harness-specific meaning, so keep their storage
with the harness. A case for separate general persistence exists if other application
areas need it, but that does not yet justify a generic storage subsystem. Sharing
low-level database mechanics later need not move session semantics or split the
selected atomic `SessionStore` authority. Credential storage policy remains open.

**Usage recommendation:** model-call usage and cost accounting belong to the harness;
UI reporting consumes those facts rather than owning their collection or retention.
Moving the folder does not tie retained totals to a live session's lifetime.

No broader class authority follows from filesystem nesting: Context does not grant
tool permission; tools do not own the assignments behind their commands; grouping
MCP with agent machinery does not give each run its own MCP connection lifecycle.
Likewise a wakeup dispatcher spans targets; it is not owned by one session execution.

**Coverage limit — partial application design, not a completed architecture:**

- UI and hosting: internal components and their collaboration remain TBD.
- Other application-level backend responsibilities: inventory and boundaries remain
  TBD. The absence of drawn folders is not a decision that the harness owns everything.
- Application settings/preferences, platform integration, setup/build/distribution:
  detailed ownership and file layout remain TBD. Do not add a generic configuration
  manager or placeholder service merely to fill the diagram.
- This does not reopen established choices (including Preact/TypeScript, ASP.NET,
  Core/Server, local-access requirements and source-with-distributables), erase earlier
  planning, or defer their requirements. TBD describes unfinished structural design.

`backend/` is the ASP.NET-free compilation boundary, not a replacement domain bucket.
Its Core project uses ordinary SDK inclusion within that subtree; the Server project
references it. This avoids the withdrawn explicit file-inclusion workaround. Subareas
from the preceding sketch still apply where useful, but empty folders are not a
scaffolding checklist. Physical packaging, exact tool pins and acquisition mechanics
remain open; the accepted rough source map is not a new build/data behavior contract.
