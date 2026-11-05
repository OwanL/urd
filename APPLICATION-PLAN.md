# Withdrawn application design proposal

**Rejected by the owner on 2026-11-05:** `systems/` is too vague. This document
preserves the rejected proposal for context; its grouping and project-layout workaround
are not the current recommendation. See [source research and alternatives](STRUCTURE-RESEARCH.md)
for the subsequent investigation and brainstorming. No replacement layout is approved.

Original proposal, 2026-11-05. **Not an approved layout or permission to implement.**
The owner asked for a new design after expanding the concern inventory. This covers
Urd as a product, not just its first runnable checkpoint. It borrows useful Pie
responsibilities without its Pi coupling, extension seams or process architecture.

**Recommendation:** organize the source by functional ownership. Keep the execution
harness small, make its supporting systems explicit, keep agent tools separate from
those systems, and give authored guidance its own editable home. Retain the selected
ASP.NET-free Core/Server compile boundary without making `Core/` a directory that
hides all those concerns.

## 1. Concerns and owners

| Area | Owns | Boundary |
| --- | --- | --- |
| **Harness** | Session identity/lifecycle, admitted input, current execution, model/tool cycle and construction of model context. | Does not manage provider accounts, HTTP, UI or every supporting service. |
| **Systems / providers** | Provider connections/auth, model catalog and bucket resolution, adapters/client construction, request policies and pricing metadata. | Supplies request-only `IChatClient`; no second agent/session engine. Session commands still decide when a model change is allowed. |
| **Systems / resources** | Discover/read bundled, user and project guidance; named-agent and skill definitions; provenance and scope rules. | Reads authored resources rather than owning their content. Exact loading/reload behavior remains deferred. |
| **Systems / capabilities** | Current tool registrations, skill/tool exposure, protected entries, selection/recovery, dispatch permissions and MCP connections. | `ToolExecutor` checks current authority. Previously exposed definitions are not permission to execute. No generic plugin framework. |
| **Systems / coordination** | Messages, assignments/results/waits, scheduled follow-ups and any retained cooperative-resource support. | Calls normal session commands. Different lifetimes remain distinct; this is not a workflow-replay engine. Resource-lease mechanics remain open. |
| **Systems / interactions** | Addressed questions/answers and other retained agent–user interactions. | The requesting execution owns its wait/cancellation; a UI client only presents and answers it. No new approval dialog policy is implied. |
| **Systems / configuration** | User/session preferences and policy inputs, including provider/model and capability choices. | Does not directly mutate live session invariants. Settings formats and credential portability remain open. |
| **Systems / accounting** | Usage/cost estimates, timing/errors, lean retained summaries and diagnostic recording. | Observes work; does not drive the execution lifecycle or retain a second conversation archive. |
| **Agent tools** | Concrete agent-callable operations and their schemas/results: files, shell, web/computer, session controls, discovery and questions. | File/shell tools can implement their operation directly. Tools exposing a shared system call that system; do not add a service layer per tool. |
| **Guidance** | Editable bundled system/task prompts, named-agent definitions, skills and supporting authored resources. | Content, not loader code or executable behavior hidden in preferences. Does not absorb user/project guidance from other scopes. |
| **Persistence & transcripts** | Shared SQLite, original transcript records and bounded reads, durable session/coordination facts, model-context records and managed attachment storage. | Domain owners decide valid transitions/retention; store operations enforce atomic writes and stale-write checks. No generic repository framework. |
| **UI** | Workspace/conversation views, drafts/navigation, provider/settings screens, questions, tool inspection and bounded client views. | Presents backend truth; never owns accepted agent work or duplicates provider/session policy. |
| **Hosting** | Process lifetime, data-instance ownership, composition, browser API/transport/access and platform entry points. | No agent business rules. All clients use the same backend/domain commands. |
| **Development & delivery** | Declared tool setup, checks, local builds, customization, matching-source distribution and clean/data boundaries. | Product workflow, not an agent/runtime subsystem or an automatic updater. |

`systems/` is a grouping of **named independent concerns**, not a `SystemsManager`
or a miscellaneous services library. Context assembly/compaction belongs with the
harness's context policy; resource lookup, catalog selection and provider-specific
representation support it without each owning a competing context. Original readable
transcripts remain distinct from the context sent to a model.

## 2. Directory layout that expresses those owners

```text
urd/
  guidance/                       centrally editable bundled content
    prompts/                      base and auxiliary-task prompts
    agents/                       named-agent definitions
    skills/                       skills and their supporting resources

  source/                         production code and scoped build policy
    harness/
      sessions/                   SessionBase, PrimarySession, SubagentSession,
                                  SessionManager; naming operations
      execution/                  SessionExecution; one model/tool cycle
      context/                    ContextBuilder, HistoryCompactor;
                                  model-context and result-reduction policy

    systems/
      providers/                  accounts, catalogs, adapters, request policies
      resources/                  guidance/agent/skill discovery and reading
      capabilities/               registrations, selection, ToolExecutor, MCP
      coordination/               messages, assignments, follow-ups, resources
      interactions/               addressed user questions and answers
      configuration/              settings and policy inputs
      accounting/                 usage summaries and diagnostics

    agent-tools/                  concrete implementations/entry points
      files/  shell/  web/  computer/
      sessions/  discovery/  interactions/

    persistence/
      transcripts/                records, bounded reads and history operations
      attachments/                managed bytes and reference-aware cleanup
      database/                   SessionStore, schema and SQLite transactions

    ui/                           shared Preact client
      workspace/  conversation/  settings/  interactions/

    hosting/                      Urd.Server.csproj; composition, HTTP, lifecycle
    tests/                        backend tests grouped by behavior owner
    Urd.Core.csproj                compiles the four backend concern roots
    Urd.slnx                      Core, Server and backend tests
    global.json                   SDK authority for this production scope
    .node-version                 proposed Node authority

  development/                    setup/check/build/package tooling as needed
  experiments/                    existing research, unchanged
  ...                             existing repository docs/license

  .tools/                         acquired tools, not distributed inputs
  builds/                         generated application/package outputs
  data/                           persistent copy data, never clean/package input
```

This is the intended organization, **not an instruction to scaffold every folder**.
Create a subdirectory only when it has substantive code/content. UI tests may live
beside their modules; one backend test project can cover several owners. Later host
entry points reuse the UI and backend rather than creating another agent application.
`source/` is a build-policy scope, not another architectural layer.

### Two backend projects, not a project for every directory

Keep `Urd.Server → Urd.Core`, with Core unable to reference ASP.NET. Put the Core
project file at `source/`, with default recursive item inclusion disabled and an
explicit source set for `harness/`, `systems/`, `agent-tools/` and `persistence/`.
It must not scoop up hosting, tests, UI or generated files. The Server project under
`hosting/` compiles that host's code and references Core; tests reference the owners
they exercise. Build scripts select the solution explicitly and anchor their cwd
inside `source/`, so SDK selection also remains scoped away from experiments.

**Trade-off:** a short explicit source inclusion list instead of the usual
project-named directory, avoiding a `Core/` catch-all or many small assemblies.
Inside Core, narrow APIs/private state and focused tests/review uphold module
ownership; the compiler enforces the ASP.NET boundary, not every internal module
edge. Split another assembly only when a concrete enforcement/reuse need earns it.
No common service base, global command bus or shared-contract project is required.

## 3. Important classes and collaboration

Preserve the existing session model rather than creating a second hierarchy:

```text
SessionManager                    finds/creates session handles; not a base class

abstract SessionBase              identity, admission/Stop and guarded updates
├─ PrimarySession                 ongoing; human- or agent-created same type
└─ SubagentSession                bounded caller-owned task

SessionBase ── owns 0..1 ──> SessionExecution
SessionExecution ── uses ──> ContextBuilder, request-only IChatClient, ToolExecutor

Session commands ──> SessionStore's intent-specific atomic operations
```

Other **working collaborator names**, not mandatory new interfaces or a class per
feature, show where responsibilities land:

| Collaborator | Home and collaboration |
| --- | --- |
| `ProviderManagement` | `systems/providers`; account/catalog/client configuration. Concrete provider adapters implement the selected request abstraction. |
| `ResourceCatalog` | `systems/resources`; returns scoped definitions/content and provenance to context assembly and discovery. No automatic reload policy is implied. |
| `ToolCatalog`, `CatalogPruner`, `ToolExecutor` | `systems/capabilities`; current registrations, selected exposure and one checked invocation path. Distinct jobs, not one generic pruning mechanism. |
| `ContextBuilder`, `HistoryCompactor` | `harness/context`; build/preserve model context. Compaction changes that context, not the original transcript. Helper calls share `IChatClient`. |
| `TitleGenerator` | `harness/sessions`; caller-owned naming attempt with guarded metadata publication, not an auxiliary session type. |
| Assignment/follow-up coordinators | `systems/coordination`; explicit work/result/deadline rules and notifications. Session admission and persistence remain shared with ordinary input. |
| `InteractionRequests` | `systems/interactions`; correlates a request with its originating execution and eventual answer/cancellation. Presentation does not own the waiting work. |
| `UsageRecorder` | `systems/accounting`; records observed usage, distinguishes missing facts from zero, and retains only the selected summaries. |
| `SessionStore` | `persistence/database`; durable session/history/context and cross-record operations. Read helpers can be separated when useful, without inventing a second transcript authority. |

Small domain records stay beside their owner. Wire records stay near their endpoint;
the frontend does not mirror the C# session hierarchy. No need to specify every method
or introduce interfaces for every test double before implementation establishes value.

### Dependencies without a circular framework

Hosting constructs the stores, supporting systems and session manager, then explicitly
registers concrete tools before accepting work. The harness depends on checked
`ToolExecutor` dispatch, **not the concrete agent-tool classes**. A session-control
tool can call the session manager's ordinary command path without the manager having
to construct/import that tool. Use ordinary object composition/registrations, not
reflection/plugin discovery or a service locator passed through the whole runtime.

The UI calls host endpoints; endpoints call the appropriate domain owner. Shared
systems never depend on ASP.NET or UI types. Provider-, tool- and operation-specific
policies remain with their owners rather than leaking into one global coordinator.
No call into a model, external tool or long wait holds a session mutation lock.

## 4. Four flows that test the design

**Edit bundled guidance.** The user edits a file under `guidance/`. Resource discovery
resolves the applicable authored content; context assembly uses it at the chosen
loading boundary, without rewriting what was already sent to the model. Guidance is
runtime-readable content, not C# string constants that require recompilation. Exact
refresh timing stays with the deferred simplest-loading decision; this adds no
mandatory watcher, Reload UI or migration of user/project files.

**Choose a provider/model.** The settings UI reads provider/model information through
the host from provider management. Authentication belongs to that system, not the
page or agent loop. Changing an existing session's model goes through the session
command/idle rules as well as provider eligibility—not a direct settings write.
Independent title/compaction/pruning requests use the same request-level client
facilities without becoming full sessions or sharing mutable request context.

**Send, execute a tool, then Stop.** UI sends an identified input; the primary session
admits it through its guard and an atomic store operation. Its execution obtains
context/client and runs the shared cycle. A tool is dispatched only after a valid
complete provider outcome and current permission checks. The concrete operation runs;
original results and model-facing representations retain their distinct meanings.
Progress publishes through guarded session writes. Further input retains the agreed
follow-up/steering semantics. Stop targets that execution and waits for actual owned
work settlement; switching tabs or losing the connection does not stop it.

**Delegate and receive a result.** A session tool calls coordination/session commands,
not SQL. Assignment acceptance and worker input are correlated in one store operation
under the worker's admission rules, so a busy worker cannot receive an independent
competing start. Explicit results are stored before notification. A bounded helper
uses `SubagentSession`; an independent worker uses `PrimarySession`. Follow-up dispatch
uses the same admission path and records dispatch evidence; interrupted/ambiguous
effects are not automatically replayed. Independent-worker cascading close remains
open rather than being selected by this directory layout.

## 5. Hosting, UI, data and delivery constraints

- One backend owns live work; clients own only their allowed presentation state.
  UI composition uses a small state hook and bounded addressed views, not a frontend
  session framework. Preserve the independent-navigation cost condition.
- Same-origin HTTP JSON plus fetch-read SSE remains a **revisable transport proposal**,
  not a consequence of the module split. Private reads/commands/streams need the
  local browser-request gate: loopback peer, actual served Host/port, non-secret
  marker header, rejection of foreign/null Origin and foreign preflights, no
  permissive CORS. Mutations are POST JSON; public assets/health expose no private
  state. Local programs remain trusted; no Urd account/pairing is added.
- A view starts from a coherent saved/live snapshot and bounded updates. Gaps cause
  a fresh read, not durable event replay. A lost Send receipt is reconciled by its
  identity, not blind resubmission. Unsent drafts are not accepted backend work.
- Host startup receives explicit resource/data roots; data ownership is established
  before opening/recovering storage. A custom copy never silently attaches to an
  unrelated copy's backend. Desktop host, normal attach/tray UX, LAN/Tailscale trust
  and installed-release data/activation remain separate open choices.
- Keep source, guidance, tools, builds and data together for the writable custom-copy
  workflow. The runtime locates its configured guidance root explicitly, not by cwd.
  Matching edited guidance and source travel with custom distributions; data, caches
  and private settings do not. This layout does not select the physical release
  container or credentials-portability policy.
- Keep .NET 10 LTS/Vite and the preferred npm route as current alignment. Supported
  pins are engineering work, not copied from installed versions. Acquire missing
  declared tools where practical without global changes; copy-local tool payloads
  remain a simple proposal with duplicate-storage cost. Locked restores and ordinary
  incremental checks are distinct from acquisition and packaging.
- Thin setup/check/build/run/smoke/dist/clean commands share the declared versions.
  Clean preserves tools/data; packaging captures fixed authored inputs without Git.
  Production settings remain scoped under `source/`, leaving research unchanged.

## 6. Scope and verification

This design covers the [broader concern inventory](ARCHITECTURE.md#review-coverage),
not a claim that all mechanisms are selected. Pie ownership references include
`harness/{tools,model-providers,agent-instructions,tool-and-skill-selection,agent-processes,session-storage}`,
`analytics/` and `application/{backend,frontend,hosts}` (read-only survey at
`cc82abb0f317f31341d3a6f12aa1545f6be1fbea`). Borrow their useful responsibilities,
not Pi dependency, JSONL transcript storage, worker topology or host-state policy.

After structural alignment and scoped code approval, first connect the actual
harness/store/host/UI with deterministic replies, then add working capabilities under
these owners. That is test sequencing, not a product cut or an idle-only Send rule.
Verify with focused fake-client/real-SQLite behavior tests and a built-UI/Kestrel
smoke, followed by targeted provider/tool/resource/distribution qualification as
those capabilities arrive. No production scaffolding, builds or runtime qualification
has occurred as part of this proposal.

**For owner review:** the proposed functional grouping, relationships and readable
source layout—not a request to freeze all helper names or approve implementation.
The existing [session](SESSION-DESIGN.md), [execution](EXECUTION-DESIGN.md),
[discovery](DISCOVERY-DESIGN.md), [browser](BROWSER-DESIGN.md) and
[build](REPO-FOUNDATIONS.md) outcomes remain authoritative for their scoped decisions.
