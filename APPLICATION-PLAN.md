# Application plan — component-led proposal for owner review

2026-11-05. **Planning, not implementation approval.** The owner rejected the
project-first `app/Urd.Core`, `Urd.Server`, `Urd.Web`, tests sketch: “I dont like it,
i say we separate the major components and parts of urd, per separation of concerns
prinsiples”. The directory proposal is withdrawn, not approved by silence.

Organize around Urd's responsibilities, then map classes and folders. The component
map below is a revised recommendation, not a newly selected hierarchy or a project
per component. The existing Core-without-ASP.NET/Server dependency boundary and narrow
session alignment still stand; the owner has not specifically reversed them.
The [foundation review](FOUNDATION-REVIEW.md) retains the other scoped outcomes.

## 1. Major components and their ownership

| Proposed component | Owns | Must not absorb |
| --- | --- | --- |
| **Agent runtime** | Sessions, temporary execution, context preparation and coordination/deferred work. Internal parts have their own owners, not one runtime manager. | HTTP/UI state, provider-specific protocols or direct SQL spread through the agent loop. |
| **Tools and integrations** | Tool catalog/dispatch, current validation/permissions, built-in implementations and MCP integration. | Session lifecycle or provider request loops. Session-control tools enter Runtime's ordinary command path, not database shortcuts. |
| **Providers** | Model catalog/access, provider adapters, provider sign-in/credentials and request-only `IChatClient` construction. | Agent sessions or another mandatory model-client facade. Runtime context policy remains separate from provider-specific representation. |
| **Persistence** | Shared SQLite access, coherent history/coordination transactions and managed attachment storage. | Agent scheduling, transport or a generic interchangeable-storage framework. Retention policy comes from the owning domain; storage enforces it. |
| **Application host** | Startup/shutdown, configuration wiring, data-instance ownership, browser API/access checks and composition. | Agent business rules or frontend state. This is the ASP.NET-dependent boundary. |
| **User interface** | Shared Preact components, navigation/drafts, bounded read models and backend connection. | Agent execution, durable truth or a frontend session-class hierarchy. |

These boundaries apply to the intended application, not only the first fake reply.
Implement only needed parts as capabilities arrive; do not pre-create empty folders.
Runtime's **Sessions**, **Execution**, **Context** and **Coordination** are explicit
subcomponents, not extra session subclasses. Tool invocation uses the current dispatch
path; provider exposure does not grant execution permission. Context still owns
prefix/history distinctions and its policy does not move into provider adapters.

Dependency direction: UI calls the host; the host composes/calls the runtime and
other components. Runtime uses provider requests, tool dispatch and persistence.
Transport cannot leak into those components. Coordination tools call guarded runtime
commands; separating folders does not permit a second admission/write path.

### Existing class alignment within that map

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

The previous directory tree is withdrawn. Do not translate the new component table
straight into six projects, processes or service interfaces. Logical components,
physical folders and compile-time dependency boundaries are different decisions.
The selected ASP.NET-free Core boundary can contain explicit functional components;
additional assembly splits need a concrete enforcement/reuse benefit.

The next directory proposal should make those components and their substantive
classes visible instead of presenting Core as an undifferentiated bucket. Folder
names, exact nesting and test placement remain open. Tests should follow behavior
owners without automatically adding a test project per component.

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

**Owner discussion:** first align the functional components and their responsibilities,
then show the class/directory mapping. The earlier request to approve the project-first
layout and begin implementation is superseded by this correction. Existing Core/Server
and session choices are not automatically reopened. Neither agreement with separation
of concerns nor this revised recommendation authorizes production code.

**Evidence:** [Session](SESSION-DESIGN.md), [Browser](BROWSER-DESIGN.md),
[Build](REPO-FOUNDATIONS.md), [backend lifecycle](BACKEND-LIFECYCLE.md) and the
[access fixture](ACCESS-BOUNDARY-EVIDENCE.md). This plan is synthesis/reasoning, not
a new executable probe or a claim that these parts already work together.
