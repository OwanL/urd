# Application plan — concerns before component layout

2026-11-05. **Planning, not implementation approval.** The owner rejected the
project-first directory sketch, then found the six-component replacement incomplete.
Both compressed the application prematurely. Start from Urd's documented concerns
and useful Pie responsibilities, not the needs of a first fake-model checkpoint.

The owner's nonexhaustive anchors are **agent tools; harness-related systems (such
as provider management); the agent harness; editable agent guidance; UI; hosting;
and persistence/transcript management**. In particular, bundled system prompts,
skills and similar authored guidance need a centralized, user-editable home.
Exact directory names, nesting and project splits remain open. The selected
ASP.NET-free Core/Server dependency boundary and narrow session alignment remain;
this correction is not production permission. See the [foundation review](FOUNDATION-REVIEW.md).

## 1. Concern inventory before grouping

Working distinction: **harness execution** is session/run machinery; **harness
systems** are named services that support it, not a replacement catch-all Core.
**Agent tools** expose operations on those owners. **Guidance** is authored content;
its loading/discovery/assembly is code with separate responsibilities. This sharpens
the owner's distinction without claiming the final placement of every class.

The following inventory expands the existing [review coverage](ARCHITECTURE.md#review-coverage).
Rows are not automatically folders, assemblies or mandatory parity requirements.
Settled behavior remains in the owning reports; open mechanisms remain open.

| Concern to make visible | Responsibility and important boundary |
| --- | --- |
| **Agent tools** | Agent-callable contracts, implementations and command entry points: files/shell, web/browser/desktop, session operations and user questions. An entry point does not acquire its own independent lifecycle/state store. |
| **Tool/integration infrastructure** | Catalog/registration, current validation/permission checks, execution safety and MCP/package connectivity. Exposure to a model is not authority to execute. No general plugin framework follows. |
| **Agent harness** | Session identity/lifetimes, admitted input, temporary runs, model/tool cycle, streaming, cancellation and settlement. Not provider onboarding, UI state or a catch-all manager. |
| **Provider/model management** | Accounts/credentials, connection/model catalogs, adapters, bucket selection, capabilities, request limits/retry/fallback and pricing support. Use shared request-level `IChatClient`, not a second session engine. |
| **Authored agent guidance** | Central editable bundled system/helper prompts, named-agent definitions and skills/resources. Content is distinct from loader code; existing user/project guidance stays in its own scope. Names and formats beyond the stated Markdown direction remain proposals. |
| **Resource discovery and catalog selection** | Locate/compose guidance and discover agents/skills/tools; protect required entries, select/prune exposure and recover hidden capabilities. Loader precedence and when edits apply must follow their own decisions, not the folder sketch. |
| **Model-context management** | Already-sent prefix/history fidelity, compaction, tool-result reduction/recall and provider-specific representation. These are distinct mechanisms, not a single generic pruning service or the visible transcript. |
| **Coordination and deferred work** | Delegation, messages, assignments/results/waits and durable follow-ups, with distinct identities/lifetimes. Cooperative resource coordination is also a review concern, not an inherited Pie lease implementation or generic scheduler requirement. |
| **Persistence and transcript management** | Shared SQLite, original readable history, coherent bounded reads/edits, durable coordination facts and truthful interrupted-work recovery. Includes attachment references/lifetimes and managed bytes; no generic storage framework or new atomicity guarantee. |
| **Configuration and policy** | User/session settings, provider/model preferences and capability choices. This is more than host wiring; exact files, precedence and storage are not selected by this inventory. |
| **Accounting and diagnostics** | Usage, estimated costs, timing/errors and useful operational visibility, with explicit privacy/retention. Retain the selected lean usage summaries, not Pie's full historical analytics machinery. |
| **User interface and interaction** | Conversation/workspace views, drafts/navigation, tool/detail inspection, questions/attention and fresh bounded client views. Does not own agent execution or durable truth. |
| **Hosting and access** | Process/backend lifetime, composition, startup/discovery/stop, browser API/transport and local/remote trust. Does not own provider credentials or agent business rules merely because it exposes endpoints. |
| **Customization, build and delivery** | Editable source/guidance, declared tools, local checks/builds, custom-copy data and matching-source distribution/activation. A product workflow with ownership, not incidental scripts. Automatic updating remains deferred. |

Verification, low LOC/resource overhead, responsiveness and privacy constrain all
these areas; they do not each require a runtime subsystem. Do not create placeholders
for every row. The inventory can grow and grouping must follow concrete collaborations.

### Pie reference, not a transplanted architecture

Read-only source survey at Pie checkout `cc82abb0f317f31341d3a6f12aa1545f6be1fbea`:
`harness/tools/`, `harness/model-providers/`, `harness/agent-instructions/`,
`harness/tool-and-skill-selection/`, `harness/agent-processes/`, `harness/session-storage/`,
`analytics/`, and `application/{backend,frontend,hosts}/` give concrete owners to examine.
Pie's Architecture ownership section and those owners' contracts are references,
not evidence about installed behavior or approval to copy their implementation.

Useful responsibilities and selected logic may transfer; Pie dependency/extension
seams, process-worker topology, JSONL storage, host-owned draft/navigation policy and
VS Code/artifact-delivery machinery are not defaults to import. Urd's selected SQLite,
first-party harness and other explicit decisions govern; remaining choices stay open.
No full Pie parity checklist is implied.

### Existing class alignment, not a complete application model

| Owner | What it does; what it must not become |
| --- | --- |
| `SessionManager` | Finds/creates one live handle per session ID and coordinates cross-session entry. Not the model loop or a second history database. Browsing does not hydrate every session. |
| `SessionBase` | Shared identity, current-execution ownership, admission/Stop coordination and stale-update rejection. Keep the already-aligned base narrow. |
| `PrimarySession` | Ongoing input and primary-session policy. Human-created and independently delegated chats use the same type. |
| `SessionExecution` | Temporary async model/tool work, pending steering and cancellation. Reports through guarded session writes; it cannot mutate history freely. |
| `SessionStore` | Intent-specific SQLite transactions and coherent bounded reads. No generic repository/ORM or event-replay platform. |
| Server handlers | Check browser requests, address commands and map views. Domain admission stays in Core; an HTTP disconnect does not cancel accepted work. |
| Browser state hook | Selected chat, per-chat drafts, bounded cached views, freshness and unresolved input IDs. No frontend copy of the session class hierarchy. |

`SubagentSession` retains the bounded-task role in the existing hierarchy; add its
implementation with that capability, not an empty placeholder now. Titles, compaction
and pruning remain operations sharing `IChatClient`, not additional session types.
Small records stay with their owner until real sharing warrants a separate file.

## 2. Map components to files after responsibility alignment

The previous directory tree is withdrawn. Do not translate the concern inventory
straight into projects, processes or service interfaces. Logical components,
physical folders and compile-time dependency boundaries are different decisions.
The selected ASP.NET-free Core boundary can contain explicit functional components;
additional assembly splits need a concrete enforcement/reuse benefit.

Before the next directory proposal, map each concern to a named owner and trace a
few real collaborations: editing bundled guidance, selecting/authenticating a provider,
running a tool and completing delegated work. Keep agent-tool adapters separate from
the services they invoke, authored guidance separate from its loader, and visible
transcripts separate from model context. Folder names, nesting and test placement
follow that ownership; no automatic test project per component.

Keep the agreed custom-copy requirements: source, generated builds and persistent
data together in a user-chosen writable root; data outside generated/distribution
inputs and preserved by rebuild/clean. Scope production SDK/build rules away from
existing experiments, without moving the research or imposing new root-wide policy.
The proposed setup still has one SDK pin (`global.json`) and one Node pin (for
example `.node-version`, with bundled npm); their paths follow the revised layout.
A thin Windows command entry, `.tools/` and `builds/` remain setup proposals, not
owner-selected directory names or physical packaging.

## 3. A concrete interaction

1. Open chats A/B. Show a bounded current view; a cached view can appear immediately
   on switching, marked stale until refreshed. History queries do not start agents.
2. Send in A. Preserve the submitted draft/identity until acceptance is known;
   clear only that draft version. Core records admission and owns the execution.
3. Select B while A replies. A continues on the backend. A late response cannot
   select A or replace B's draft. New typing is not lost with an old acknowledgement.
4. Further input follows the retained preflight-follow-up/safe-boundary-steering
   semantics. Internal checkpoint order is engineering, not an idle-only product cut.
5. Stop addresses the current run; show stopping until owned work actually settles.
   Old output or Stop requests cannot affect a replacement execution.
6. Reconnect from current saved/live truth, not command replay. Resolve uncertain
   Send receipts using their existing identities. After a process restart, saved
   partial work is interrupted; a missing effect result stays unknown, not retried.

## 4. One proposed local transport

Recommend same-origin **HTTP JSON + fetch-read SSE**, not parallel transports.
Commands/receipts stay ordinary request-response; selected-chat updates start with
one coherent saved/live snapshot and then bounded updates. Core coordinates capture
with subscription so no update falls into a read/subscribe gap. Overflow resets the
view from a fresh snapshot, rather than building durable event replay.

Every private read, command and stream requires a fixed **non-secret** request header,
validated loopback peer and actual served Host/port; reject supplied foreign/null
Origin and foreign preflights, without permissive CORS. Mutations use POST JSON.
Static assets/health expose no private state. This is a browser-request boundary,
not client authentication; native local programs remain trusted. No pairing/accounts.

Cost: fetch needs bounded SSE decoding/reconnect because native EventSource cannot
set the header. An exact-Origin-gated WebSocket is the credible alternative: simpler
stream framing, but request/response correlation and socket handling. Start with one
implementation; revisit if the actual glue outweighs the proposed simplicity.
Built Vite assets are served by Kestrel; no separate Node application server is needed.

## 5. Setup and copy ownership

Use .NET 10 LTS, Vite and the preferred npm route. Choose supported compatible pins
as engineering work, not from the tools installed on this machine. Read version
pins from the eventual scoped version files, not duplicate script constants. Acquire missing
declared SDK/Node payloads into versioned `.tools/` using official distributions;
validate completion/version and use explicit executables with child-only environment.
No global PATH changes, automatic app updates or custom tool-manager framework.

Per-copy tools simplify ownership/isolation but duplicate SDK storage/downloads.
A shared tool cache saves that space but adds population/cleanup coordination; do not
build one speculatively. Ordinary npm/NuGet dependency caches can remain shared.
Tool acquisition is separate from locked restores. Reuse unchanged restored inputs
for fast checks; an explicit restore repairs them. All commands anchor their cwd.

Suggested command surface: `setup/restore`, `check`, `build`, `run`, `smoke`, `dist`,
`clean`. Check covers compilation/types and fast behavior tests; smoke and source
redistribution are separate. Build emits no source archive. Clean removes declared
generated output only, preserving tools/data. Distribution captures with writers
paused, builds/packages those fixed inputs and carries edited source/locks/recipes/
instructions without Git, excluding data, caches and previous outputs/packages.

Server startup receives the explicit absolute copy-data root before opening storage.
For the first developer run, print that server's actual loopback URL and keep it in
the foreground; do not silently attach to some other copy's backend. Enforce one
owner of the data directory before recovery/admission; a second start reports the
conflict rather than stealing ownership or killing a PID. `BackendInstance` is this
small Server-owned responsibility, not a process-management platform. Normal-use
attach/discovery, tray and installed-release data/activation remain separate work.

## 6. Build order and acceptance after scope approval

1. Establish the scoped project/config boundary and shared test fake/barriers.
   Implement the primary session/store path through actual classes, not copied
   prototype contracts. Use disposable test data and explicit schema-version handling
   before retaining real data; incompatible data is not silently reset. No empty future
   classes and no additional model-client facade.
2. Join those paths to thin Server handlers and the actual Preact UI. Demonstrate
   fake replies, switching, saved history, reconnect and settled Stop; preserve the
   retained input semantics rather than treating an internal checkpoint as the product.
3. Use focused behavior/real-SQLite tests plus one built-UI/Kestrel smoke with the
   fake injected at Server composition. Core has no test-assembly dependency.
   Check lost receipts, stale callbacks, consistent bounded reads and interrupted
   reopen without automatic model calls. Independently qualify the browser gate.
4. Qualify missing-tool setup, differently pinned copies, data surviving rebuild/
   clean and data exclusion from distribution. Existing warm-cache fixtures do not
   prove those paths. Source packaging is not required on every development build.

Real providers/tools, context fidelity, delegation and normal background operation
enter as subsequent working capabilities, not abandoned features. There is no claim
that the fake-model checkpoint is ready for normal use. Detailed schema/method names,
lock primitives and test sequencing are implementation work, not owner questionnaires.

**Owner discussion:** the six-way map was incomplete, not a finished structure awaiting
a yes/no vote. Develop the full concern/ownership map with the owner, using Pie where
helpful; then show the class/directory mapping. The earlier implementation-approval ask
remains withdrawn. Existing Core/Server and session choices are not automatically
reopened, and agreement with this direction is not production-code authorization.

**Evidence:** [Session](SESSION-DESIGN.md), [Browser](BROWSER-DESIGN.md),
[Build](REPO-FOUNDATIONS.md), [backend lifecycle](BACKEND-LIFECYCLE.md) and the
[access fixture](ACCESS-BOUNDARY-EVIDENCE.md). This plan is synthesis/reasoning, not
a new executable probe or a claim that these parts already work together.
