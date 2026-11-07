# Urd architecture and feature decisions

## Status and scope

This is a living design record, not an implemented architecture. The project
purpose and strategic constraints are owned by [README.md](../README.md).

The review includes feature value as well as interfaces, data models,
dependencies, class/module structure, ownership, coupling, failure behaviour
and performance. Pie provides the starting inventory; it is not a mandatory
parity checklist. Retaining, simplifying, replacing and removing features are
all valid outcomes when explicitly justified.

Only decisions recorded as agreed are settled. Review topics, observations
about Pie and recommendations do not select an implementation by themselves.

## Review method

**Protect the owner's attention.** Preserve clearly useful, settled Pie
capabilities by default; do not turn the inventory into a checklist of requests
to reapprove obvious basics. Investigate routine implementation choices
independently. Ask only about genuinely ambiguous, consequential trade-offs,
conflicting requirements or meaningful changes/removals. Bring evidence, explain
the practical consequence in plain terms and recommend an answer. Feature value
remains open to challenge, but questioning it must earn the interruption.

**Recorded answers are constraints, not a reapproval checklist.** During alignment
audits, challenge assistant-added assumptions and actual contradictions. Do not
re-ask settled questions merely to reconfirm them. Reopening needs material new
evidence or an owner request to revisit that specific decision, not merely a
general request for an alignment audit. Explain the conflict, record answers as
they are given and consult them before asking again.

During this architecture review, use the main conversation for orchestration and
owner alignment, with bounded independent investigations running separately.
Research workers provide evidence and trade-offs, not implementation commitments.
Distinguish owner requirements from assistant inferences: a broad requirement
must not silently become a more restrictive architectural rule. Consequential
choices need alignment before being recorded as agreed.

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

Broader feature, data and operational discovery must inform costly foundational
choices. A narrow prototype can strengthen a candidate without making the project
ready to select it. Focus owner questions on meaningful product trade-offs, not
on specifying routine implementation details or unnecessarily strict edge-case
guarantees.

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
| Lifecycle and delivery | Backend startup/discovery, background controls, source customisation, development builds and packaging. Automatic updating is deferred; agent-guided upgrades remain user-managed. |
| Access and trust | Single-user local/remote access, credentials and permission boundaries; shared multi-user hosting is excluded, and sandbox isolation must not be assumed. |
| Verification and performance | Behavioural acceptance, representative workloads, failure testing and dependency/complexity costs. |

## Agreed architectural decisions

The existing strategic decisions remain in the README: a Urd-specific harness
without Pi or another complete agent runtime as its foundation; single-user,
local-first deployment; a shared backend with multiple UI clients; background operation
after UI closure; source-based customisation; Windows → macOS → Linux delivery;
and no Pie data-compatibility requirement. More detailed decisions will be recorded
here as the review progresses. Keeping LOC down is also a strict project
requirement; see the README's code-size constraint.

### C# backend and class design

**Decision (2026-11-03):** Use C# on .NET for the backend. The owner's rationale
is readability when strong class structures are designed deliberately. Backend
language comparison is settled; earlier shortlist recommendations are historical.

**Design direction:** Plan classes around Urd's needs, with clear responsibilities,
state ownership, relationships and lifecycle rules. Resolve those concerns before
committing to concrete class names or hierarchies. Strong structure means an
understandable model, not maximising class count, inheritance or abstraction layers;
the strict code-size requirement remains in force.

**Planned design work:** For each area reviewed, identify its core concepts and
behaviour, which classes own state and enforce rules, and how they collaborate.
Check the proposed structure against representative flows, including cancellation,
restart and closure where relevant. Record a concise class/responsibility sketch
as the design becomes concrete; introduce interfaces and shared abstractions when
actual boundaries or reuse justify them, not as a framework imposed in advance.
No concrete class layout is approved by this decision.

**Owner alignment (2026-11-04):** Involve the owner in overall application,
class and file structure before treating a design as agreed. Class responsibilities
and relationships should drive the filesystem layout, not the other way around.
Use concrete flows and small sketches to discuss meaningful alternatives; parallel
investigations and experimental code do not approve a production scaffold or
project/folder split.

**Owner correction (2026-11-05):** the owner rejected the proposed directory sketch
organized mainly as `app/Urd.Core`, `Urd.Server`, `Urd.Web` and tests, asking to
separate Urd's major components/parts according to separation of concerns. Make
functional responsibilities and dependencies explicit before mapping folders;
project names alone are not that design. This does not select one assembly/process
per component or specifically reverse the existing Core/Server dependency boundary.
The subsequent six-way map was also incomplete. The owner explicitly distinguished
agent tools, harness-supporting systems (such as provider management), the harness
itself, editable agent guidance, UI, hosting and persistence/transcript management,
and said the list could continue. These are organizing concerns, not a closed
hierarchy or one project each. Borrow useful organization/responsibilities from Pie,
not its historical coupling or every feature/mechanism.

**Guidance authoring direction:** provide a centralized, user-editable place for
bundled Markdown system prompts, skills and similar guidance. Authored content and
the code that discovers/loads/assembles it are separate concerns. This does not move
existing user/project guidance into that location, choose exact paths, or settle
reload timing. Configuration, context/catalog management, coordination/deferred work,
usage/accounting and source customization must also stay visible in the wider design.
After agreeing with this broader direction, the owner requested a new design, then
rejected the resulting [application proposal](APPLICATION-PLAN.md): **“Systems is
far too vague”**, and requested actual research and brainstorming. That layout is
withdrawn, including its project-file placement workaround. The subsequent
[source investigation and alternatives](STRUCTURE-RESEARCH.md) compare concrete
state/lifetime owners in Pie, OpenCode and Codex, and domain modules versus feature
slices versus a reusable harness split. Its working recommendation and class sketches
are proposals, not new decisions. Do not merely rename or flatten a catch-all bucket;
prove that changes have understandable owners. The selected Core/Server boundary
remains; no final structure or production scaffold is approved.

**Subsequent structure feedback (2026-11-05):** the owner finds the later component-led
tree broadly acceptable structurally, but requests clearer names and grouping. In
particular, `guidance/` versus `agent-instructions/` does not distinguish authored
content from loader code clearly; “agent instructions” is not the preferred top-level
name for the editable Markdown/skills collection either. Model context, agent tools
and similar responsibilities belong under the broader harness, rather than requiring
peer folders beside it. Use `execution` for the narrow model/tool loop. The owner
subsequently accepted this nesting, rejected `agent-content/` as misleading, and prefers
“guidance”, “instructions” or similar with matching loader terminology. “Collaboration”
and “follow-ups” also need concrete names. Usage accounting is expected within the
harness; session storage placement is acknowledged as arguable. The owner warns that
harness detail must not masquerade as a complete application design: less-developed
areas can be marked TBD.

**Rough-structure acceptance (2026-11-05):** the owner accepts the resulting tree as a
decent starting point, explicitly open to correction during implementation, and asks
for centralized repository areas similar to Pie, particularly `docs/`. The
[application plan](APPLICATION-PLAN.md) is now the canonical working layout: guidance
with matching loader terminology, session messaging/assignments/wakeups, and harness
storage/usage. The previous project-first and `systems/` designs remain superseded.
The owner subsequently requests flat `docs/`, optionally `docs/scratch/`, rejects
`development/` as vague and drops `experiments/` from the target layout. The baseline
uses `scripts/` for executable automation; shared assets remain conditional. The
owner subsequently authorized moving the existing experimental tree to machine-local
scratch; [the verified archive](../README.md#experimental-source-archive) preserves its
files and provenance. Documentation remains in place. Routine naming/nesting corrections
can follow real implementation needs; this is not a frozen class, project or persistence
contract.

UI, hosting, other application-level concerns and development/delivery internals remain
explicitly TBD where not worked out; existing technology and behavioral decisions
still stand. Component state ownership, shared lifetimes and the Core/Server boundary
are unchanged. Rough-layout approval is not authorization for a production scaffold.

The [working session sketch](SESSION-DESIGN.md) below records subsequent owner
input, including the selected, revisable Core/Server boundary; it does not approve
final APIs, broader base-class scope, fine file/folder layout or a production scaffold.

**Subsequent owner alignment:** .NET 10 LTS is the backend version direction,
not an exact SDK pin or approval of the complete build baseline.

**Still open:** exact .NET SDK pin, provider-specific integrations, execution
topology and desktop host. Shared SQLite, `IChatClient` and TypeScript/Preact are
selected below; the narrow Urd-owned loop is provisional. These directions do not
qualify performance, provider coverage, packaging or cross-platform behaviour.

### ASP.NET Core hosting and HTTP

**Decision (2026-11-03):** Use ASP.NET Core with its standard Kestrel server for
backend hosting and HTTP. The hosting comparison is settled; see
[the research](HOSTING-RESEARCH.md) for alternatives and evidence.

The owner's acceptance criterion is working behaviour, not Microsoft's support
commitment for a particular deployment. The macOS production-hosting policy note
is not evidence that LAN/Tailscale connections fail and must not be treated as a
selection blocker. Verify the actual supported Urd workflows on each OS; remote
access on macOS remains intended scope. No macOS runtime test has been performed.

Minimal APIs remain the recommended initial endpoint approach, not a separately
locked choice. Exact .NET SDK pin, endpoint/event protocol, remote access policy, packaging
and process topology remain open. Agent work must retain its own lifetime rather
than being cancelled by an HTTP request or client disconnect.

### MCP client library and revisiting library choices

**Decision (2026-11-04):** Use the official C# MCP SDK's
`ModelContextProtocol.Core` for MCP client protocol and transport mechanics.
[The research](MCP-LIBRARY-RESEARCH.md) found no evidence-based blocker; qualify
required servers and transports during implementation. Its MEAI abstractions
dependency does not select a model/tool loop or model exposure policy; the
separate `IChatClient` decision is recorded below.
Urd retains connection lifetime, credentials, permissions and discovery/exposure
responsibilities. The inspected package version is not a locked dependency version.
[Bounded SDK qualification](MCP-QUALIFICATION.md) exercises real Core 2.3.1 with
fake stdio peers: explicit 2025 handshake, result/error fidelity, current-authority
checks and local cancellation/disposal. Default 2026 negotiation/fallback,
real-server interoperability and production lifetime integration remain untested.

**General library-selection policy:** These are implementation directions, not
irreversible commitments. If a selected library proves cumbersome, unreliable or
requires disproportionate integration code, reconsider it and compare an alternative
approach against concrete evidence. Do not force Urd's design around a poor fit or
build interchangeable-backend frameworks merely to anticipate replacement. This
policy applies to selected libraries/frameworks generally, including ASP.NET Core.

### Browser-first delivery; develop from Pie until Urd is ready

**Decision:** Target the browser for the first usable Urd client. Pie remains
the development environment until Urd is ready for normal use. This removes
pressure to add desktop/editor integration immediately or cut essential features
just to begin developing Urd from itself. Desktop and VS Code clients remain
part of the destination; desktop host and host-specific integration remain open.

**Subsequent owner alignment:** deliver normal-use browser access on the backend
machine first, then LAN, then Tailscale. This sequence alone did not select
transport, client bootstrap or provider onboarding and is distinct from the smaller
fake-model technical slice.

**Local access direction:** keep Pie-like open-and-use access, with programs already
running on the backend PC trusted. No Urd account, pairing or remembered-browser
approval system. Use loopback and strict, transport-appropriate browser-request
checks; this is local trust, not individual-client authentication or permission for
arbitrary web origins. Exact request checks and transport remain engineering design;
unauthenticated LAN or application trust in a future Tailscale/proxy setup is not
selected. See [browser design](BROWSER-DESIGN.md).

### TypeScript and Preact for the shared UI

**Decision (2026-11-04):** Use TypeScript + Preact. After discussing the trade-off,
the owner answered “Yes, proceed with it.” Prefer ordinary browser components and
presentation logic reusable in later desktop/VS Code hosts, without inheriting
Pie's host-owned state or runtime machinery. This adds a Node source-build toolchain
alongside .NET; prebuilt UI assets do not require a Node application server.

[Browser design](BROWSER-DESIGN.md) records a bounded synthetic transcript/streaming
experiment and a small Blazor contrast, not a production performance comparison.
Real SQLite/agents, rich rendering, network access and host reuse remain unqualified.
**Subsequent owner alignment:** Vite for frontend builds; npm is the preferred,
revisable package-manager route after “Lean toward npm”, not an unconditional
full-baseline selection. Node version, exact pins and setup mechanisms remain open;
see [repository foundations](REPO-FOUNDATIONS.md).

**Not selected:** CSS, remaining build-tooling details/pins, transport, desktop host,
client-state classes/responsibilities or project/folder layout. The proposed client-state
split was not clear to the owner and remains explicitly provisional; review a concrete
example rather than interpreting non-objection as approval.

### Operating-system rollout: Windows, then macOS, then Linux

**Decision:** Plan for all three operating systems in that order. This is distinct
from delivering browser, desktop and VS Code clients incrementally. Platform
versions, Linux environments, timing and detailed feature parity remain open.

Evaluate portability of the complete stack, not only its language/runtime:
backend and UI reuse, desktop hosts, credentials, shell/process management,
background controls, browser/desktop automation, packaging and local source builds.
OS-specific APIs are legitimate integration points, not evidence that the shared
core must be platform-specific. The first foundation ranking was Windows-centric;
its existing scores do not qualify the full rollout. See
[the portability correction](FOUNDATION-RANKING.md#portability-correction-windows--macos--linux).

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

**Evidence, not a waived gate:** [Browser design](BROWSER-DESIGN.md#independent-navigation-cost-condition)
compared executable independent/mirrored variants: a normalized conservative
10 added lines demonstrated the condition for that synthetic prototype. Recheck
the complete production integration against the **100 incremental LOC** condition;
independence is not unconditional scope. Do not build configurable modes merely
to support both.

**Unsent drafts:** Choose whichever ownership is simplest. Separate drafts per
client are the preferred working default, not an unconditional requirement.
Cross-client hand-off of unfinished text/attachments is not required, and neither
is a choice of draft modes. Avoid synchronisation/conflict machinery merely to
share drafts. This does not select draft storage or reload persistence.

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
design question.

**Automatic fallback:** Preserve Pie's enabled-provider/bucket selection and
fallback workflow for delegated work: bounded subagents and independent delegated
sessions. Switching within that configured policy does not require repeated
approval, including when it selects an already-enabled metered provider. Do not
silently widen the allowed provider pool. This decision does not choose automatic
fallback for a user-selected model in their own chat.

Creation-time selection and in-run retries are distinct paths to inspect, not
proof that Pie implements both delegation forms identically. Detailed eligibility,
retry and selection mechanics still need review; retaining the useful workflow
does not approve every current implementation detail or require a new
spending-approval system.

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
This does not require separate agent engines or storage implementations. The
working `SessionBase`/`PrimarySession`/`SubagentSession` sketch below shares core
logic, without freezing APIs or every cancellation/ownership rule. The
creator-cleanup boundary below remains agreed.

**Creator cleanup after human steering:** Keep Pie's behaviour. Direct human
steering does not remove the creator agent's authority to close its worker; an
idle human-steered session remains eligible for parent cleanup. Retain the
running-session skip by default and instructions to collect results and check
human use before closing. Do not add a hard takeover/protection rule merely
because the user has spoken in the child chat. Urd's final-closure policy still
applies; this does not inherit Pie's normal-session history retention.

### Owner decision (2026-11-07): Stop owns the bounded work tree

**Required product contract:** Stop in the main session must cancel its current
work, including tools, bounded subagents and all descendants of those subagents.
No owned work may silently continue or be reported as “Stopped” while still live.
Separately opened independent worker chats continue: creation by the stopped
session or later human steering does not bring them into its Stop scope.

Cancellation requested is not cleanup complete. Keep the session stopping/fenced
until owned work is confirmed stopped; admit no overlapping replacement. Future
tool/child implementations must own and join their nested work, forbid untracked
background work and reject late child admission after stopping begins. A cancelled
token or disposed handle alone does not confirm that an external process exited.

Stop is not close/delete or recovery: it neither deletes history nor reverses
completed effects, and uncertain results must not be replayed as though nothing
happened. Independent-worker close and creator-cleanup policies above and below
are unchanged; this decision does not settle the open independent-close question.

**Implementation boundary:** Current runtime scope is primary-session/model work
and configured fake `AIFunction`s only. This records the required contract, not
implementation or qualification of bounded-subagent/process-tree cancellation.
No future classes or frameworks are promised.

### Keep conversation history linear

**Decision:** A session has a linear conversation, not a tree of revisitable
alternative branches. Editing an earlier prompt replaces the later conversation,
as Pie currently does. Do not inherit Pi's conversation-tree machinery for
branch-navigation functionality Urd has not chosen to offer.

**Rationale:** Simple ordered history is sufficient for the intended workflow
and reduces the state that storage, context construction and recovery must
interpret. In Pie, the UI does not expose branch navigation, and the edit path
rewrites the saved transcript before submitting the replacement prompt.

**Boundary:** This is history replacement only, not rollback. Editing or removing
conversation records does not undo filesystem, network or other external effects
already performed by tools. Exact mutation,
concurrency and interrupted-edit rules remain to be designed.

### Retain assignments and results across backend restarts

**Decision:** Persist assignment records and their results, not only conversation
history. The records must retain enough identity, participant and status
information to establish which work was requested and what outcome was recorded.

**Rationale:** Sustained agent coordination should not lose its basic facts when
the backend restarts. Pie's coordinator-lifetime task registry does lose that
evidence; Urd will deliberately improve this behaviour.

**Boundary:** Remembering work is not the same as resuming execution. Stored
assignments are not permission to automatically repeat tool side effects.
Recovery must distinguish recorded results from unfinished or uncertain work;
the exact state transitions and resume controls remain to be designed. This is
not a commitment to a general workflow-replay engine.

### Responsiveness is a product-wide requirement

**Decision:** Urd must feel responsive throughout its UI. In particular,
clicking a session tab must reveal its actual text/content with minimal delay.
Session-load latency is a primary design criterion, not secondary polish.

**Measurement boundary:** Measure click-to-usable-visible-content, not merely
click-to-acknowledgement or click-to-loading-indicator. Distinguish warm sessions,
cold sessions and startup, and include long histories and large tool results.
Separate storage retrieval, decoding/projection, transport and rendering costs
so a fast query is not mistaken for a fast user experience. Numeric targets and
representative workload sizes still need to be established.

**Consequence:** Storage, data representation and UI delivery must be evaluated
together. Active streaming and background work must also be included when
checking responsiveness. This requirement does not select a storage engine or
approve a particular cache, paging or rendering framework.

### Practical crash recovery: coherent continuation, not exact loss budgets

**Decision:** Losing a reasonable amount of recent progress on a backend crash
is acceptable, provided the saved session remains coherent and can be continued
after restart. Losing a tool call that was still being drafted is an acceptable
example. The requirement is usable continuation, not preservation of every token
or a fixed maximum number of seconds of lost work.

A fatal shared-runtime crash may interrupt all active agents together. The owner
accepts that risk in favour of a simple, lightweight design; it is not a reason
to require per-agent isolation or complex crash supervision. Avoid crashes and
contain ordinary errors, but do not build an elaborate recovery framework or
require the owner to specify every persistence barrier. Choose straightforward
implementation mechanics against the practical requirement. No mandatory per-token checkpoint,
exact write-ahead protocol or whole-machine power-loss guarantee has been chosen.

Remembering saved assignments/results remains required. A missing tool result
does not prove an external action never happened; recovery must not blindly
repeat potentially completed effects just to make the transcript look complete.

### Scale to substantial fan-out without treating estimates as limits

**Direction:** Support as many agents as is practical while remaining performant.
The owner's speculative sizing examples are up to 10 user-managed sessions,
each spawning approximately four bounded subagents or four independent sessions
that can themselves each run approximately four subagents.

Useful exploratory scenarios are therefore roughly **50 live agents** for direct
fan-out, and **210 live agents** for the deeper example (10 + 40 + 160). These
are workload examples, not hard caps, guaranteed capacities or a promise that
all agents issue provider requests simultaneously. Waiting, actively generating
and tool-executing agents impose different resource demands; saved/idle history
is another dimension entirely.

Evaluate memory, request admission, storage and UI responsiveness under fan-out.
Do not introduce an arbitrary global agent limit from these estimates, or assume
that supporting many contexts requires a process per agent or distributed
infrastructure. The provisional process model remains provisional.

### Keep subscription, API-key and local-model access

**Decision:** Retain all three model-access methods:

- Subscription sign-in, such as ChatGPT or GitHub Copilot access.
- Provider API keys.
- Connections to local model servers, such as Ollama.

**Popular subscription support is intended scope**, not merely one arbitrary
vendor to satisfy the sign-in category. Prefer existing libraries for standard
authentication and token management rather than rebuilding those mechanisms.
This does not select a library or promise every model/vendor combination;
authentication alone does not establish subscription access to a model API.
Connecting to a local server does not mean building Urd's own model runner.
**Claude is API-access-only:** retain API-key access, not Claude consumer-plan
subscription integration. Other popular subscription routes still need their own
qualification; this exception does not remove the wider subscription requirement.
Remaining provider coverage and connection management are unresolved.

**Provider sign-in is direct, with locally managed credentials.** Reuse focused
libraries without introducing a required hosted identity/token broker such as
Auth0 Token Vault. The credential store and authentication of remote UI clients
remain separate, unresolved choices.

Removing Pi includes replacing its provider/auth interfaces, not only its agent
loop. Compare independent libraries and focused adapters before choosing this
layer; do not assume an SDK for API-key requests also handles subscription
sign-in. First-party harness ownership does not require hand-writing all provider
code, and these requirements do not select a language or SDK.

[Provider research](PROVIDER-RESEARCH.md) compares model-client approaches and
subscription integration seams. Current OpenAI documentation offers a public
ChatGPT-plan Responses API flow; Pi's existing Codex route is not the only option.
GitHub's documented Copilot SDK instead embeds its agent runtime, so it must not
be mistaken for a neutral model client. These are source findings, not qualified
Urd integrations or a reason to select a library prematurely.

[Provider qualification](PROVIDER-QUALIFICATION.md) records the later focused
route/authentication findings. ChatGPT-plan Responses is a recommended first
integration, with Ollama also acceptable to the owner; no live sign-in, entitlement
or inference/tool round trip has been qualified. Its plan-route restrictions must
not be inferred from generic API-key Responses capabilities. The report now includes
[offline candidate OAuth/refresh evidence](PROVIDER-QUALIFICATION.md#offline-auth-follow-up--2026-11-04),
not production dependency approval; live access, refresh identity/races, credential
storage and revocation remain separate gates.

### Keep small usage summaries after closure

**Decision:** After a session closes, keep only a compact usage summary, such as
tokens, estimated cost, time and errors. Do not retain the conversation or full
tool outputs as historical analytics. This preserves small totals and simple
comparisons without carrying forward Pie's broad capture/query machinery.

Keep analytics lean while a session is open too. The retained tool input/output
hover view does not require a separate historical analytics system. Results
still needed by another session remain available to that session; this policy
does not discard shared coordination facts that still serve live work.

**Evidence standard:** Look for actionable insights from real data, not merely
interesting counts or charts. For each finding, identify the decision it could
support, coverage and uncertainty, and the minimum data needed. Distinguish facts
from causal claims and identify questions the records cannot answer. The initial
bounded read-only investigation is complete; its findings and limits are recorded
under [analytics investigation](#analytics-investigation-observed-value-and-limits).

More detailed tracking would need a concrete use that justifies its cost; do
not retain it merely for parity. Exact summary fields, retention duration and
cleanup timing remain implementation/design work. This decision does not select
storage technology or authorize copying Pie's analytics process topology.

### Closed sessions are finished, not a resumable archive

**Decision:** Closure is final. Losing the closed session's conversation and tool
history is intentional, not merely hiding its tab; it is not retained for reopening.
Use snooze when returning later is intended. Do not add a readable/resumable
archive merely because storage could support one. The agreed post-closure record
is a small usage summary, not the conversation or full tool outputs. Any further
retention needs another explicit requirement, not a default assumption that all
history lives indefinitely.

This concerns closing a session, not closing a UI client: the agreed background
backend behaviour remains unchanged. Assignment/result facts still needed by
another open session remain available; closing one session does not discard them
or reverse their agreed persistence across restarts. Exact cleanup timing,
including when shared facts are no longer needed, remains to be designed.

### Keep session snoozing

**Decision:** Keep snoozing; the owner finds it useful. An idle chat can be hidden
until later, then its tab returns. Snoozing is not closing: preserve the session
and its original readable history rather than applying closed-session cleanup.

Keep the distinction from both stopping work and scheduled agent follow-ups.
Snooze does not pause a running job, and restoring a tab does not itself start AI
work or take focus. Preserve the useful restart/early-restore behavior without
assuming Pie's exact presets, storage or admission machinery must be copied.
Interactions with pending work and follow-ups need a clear lifecycle rule; do not
silently bypass the existing idle-only safeguard.

### Keep AI-based tool and skill pruning, with protected core capabilities

**Decision:** Retain Pie's AI-based tool/skill catalog pruning. The owner reports
that it has been a great success. Do not replace it with only a small fixed core
and on-demand discovery; that replacement proposal was rejected.

Core/pinned/always-keep tools and skills remain available and are **excluded from
the candidate catalog before it is sent to the pruner**, not merely restored
afterward. Pie already implements this boundary and also enforces the protections
when applying selection results. Preserve useful discovery/recovery alongside
pruning; it is not a replacement for the retained selector.

Availability of skill metadata is distinct from automatically loading every full
skill body. Catalog pruning is also distinct from shortening tool results and
summarising conversation history. Do not conflate these controls or infer net
latency/token savings from the owner's positive experience. Retain the useful
behaviour without automatically copying all cache, retry and lifecycle machinery.

### Preserve model-visible prefixes during ordinary continuation and discovery

**Direction (2026-11-03):** Preserve already-sent model-visible content during
ordinary continuation and dynamic capability discovery so Urd does not needlessly
invalidate provider prompt caches. Append-only conversation storage alone is not
sufficient: system instructions, tool definitions/order and provider serialization
also affect the cached prefix. This concerns cache eligibility, not guaranteed hits
or savings. Explicit history edits and compaction remain supported; exact reset
boundaries still need design, not a blanket immutable-session rule.

**Subsequent owner alignment:** an explicitly chosen idle model/provider switch
need not block for confirmation merely because the old provider's private reasoning
cannot transfer. This does not permit losing visible messages, tool results or
attachments, replaying tools, automatic provider fallback or widening billing scope.
No particular warning UI or running-turn change rule was selected. See
[discovery design](DISCOVERY-DESIGN.md) for the scoped owner answer.

**Project-instruction loading:** the owner first preferred ignoring edits until
explicit reload, then deferred exact loading/reload behavior to the simplest
implementation once the trade-off is concrete. Reload remains a simple candidate,
not a newly mandated per-chat feature or fixed scope. No watcher, live-loading
subsystem or frozen-forever session policy was approved. Ordinary prefix stability,
readable originals, deliberate context changes and current permission enforcement
remain distinct constraints; see [discovery design](DISCOVERY-DESIGN.md).

Pie freezes its native catalog and appends recovered tool descriptions, routing
calls through a fixed wrapper; see `docs/plans/IMMUTABLE-MODEL-CONTEXT.md` in Pie.
That is evidence of a working design path, not a requirement to copy the wrapper
or proof that all providers require it. Claude now documents beta
[mid-conversation tool definitions](https://platform.claude.com/docs/en/build-with-claude/mid-conversation-system-messages)
that can append tools without rewriting the earlier prefix, subject to provider
restrictions. This is documented capability, not a qualified Urd integration.

Shared model interfaces and optional loop libraries must be evaluated for stable
request construction, dynamic discovery and lossless handling of necessary
provider-specific content. A common interface must not erase these controls.
Check actual serialized request sequences before claiming compatibility; real
cache reuse needs provider evidence. The later interface and exposure decisions
below do not qualify every route. Cached descriptions must not override current
execution permissions.

**Follow-up alignment (2026-11-03):** The [cache investigation](CACHE-RESEARCH.md)
supports the original concern: changing early tool definitions generally loses
prefix reuse across the later conversation, not necessarily the earlier unchanged
prefix or the old cache entry itself. Urd-specific savings remain unmeasured.
Continue dynamic tool discovery design and use its requirements to inform
model-layer selection.

**Owner alignment (2026-11-04): qualified native additions, otherwise an initial
stable wrapper.** Where the provider **and adapter** support qualified native
appended definitions, use them and omit the invocation wrapper from the initial
catalog. Otherwise include a stable wrapper from the start and disclose recovered
tools through appended context. Ordinary tool-list updates remain an alternative,
**not the selected fallback**. Initially selected native/core tools need not all
be wrapped. Both paths retain AI pruning/protected core entries and the same
current-permission execution path; preserve outer call and resolved tool identities.

This supersedes the earlier open fallback comparison, not the qualification gates.
Wrapper reliability, native-provider acceptance and cache savings remain unmeasured;
no generic discovery framework or particular beta adapter is approved. See
[execution evidence](EXECUTION-DESIGN.md) and [discovery design](DISCOVERY-DESIGN.md).
This tool-invocation wrapper is distinct from a model-client facade.

**Owner decision (2026-11-04): adopt `Microsoft.Extensions.AI.Abstractions` and
its `IChatClient` as Urd's shared model request/response interface.** This is the
chosen chat-library boundary for provider calls, not a requirement to force
provider-specific features through lossy representations. Urd may use thin
provider-specific adapters or retain native request/response data where needed.
The implementation can be reconsidered if concrete integration proves cumbersome
or a poor fit, under the general library-selection policy above. The researched
package version is not locked.

This interface decision does **not** select the full `Microsoft.Extensions.AI`
package, `FunctionInvokingChatClient`, provider-specific SDKs/adapters or
authentication flows. The separate provisional loop direction and exposure policy
are recorded here; neither follows automatically from `IChatClient`. The
[discovery design](DISCOVERY-DESIGN.md) records adapter/restart-representation gaps;
address them without silently broadening the interface selection.

### Keep original chat history until the session closes

**Decision:** Original messages remain readable while a session is open, even
after older conversation has been summarized for the AI. Context compaction
changes what the model works from; it does not erase the user's readable history.

These are different views of the session, not a requirement to duplicate every
message. Opening or switching a tab must not require loading the entire original
history; older content can be fetched as needed. This requirement informs future
storage/read-path comparisons without selecting a data engine.

An explicit edit to an earlier prompt still replaces the later conversation as
already agreed. At closure, the small-usage-summary retention policy applies;
compaction does not create a separate permanent archive.

### Keep tool input/output inspection on hover

**Decision:** Hovering a tool call to see its input and output is sufficient for
now. Keep that straightforward interaction; no additional inspection screen or
separate debugging feature has been requested.

The reference is Pie's existing hover interaction. Do not interpret this as
approval for a new capture/analytics system, or as a decision to remove useful
information from the existing view. Exact capture and representation mechanics
remain implementation questions; there is no need to make the owner decide them
through competing technical labels for this same interaction.

Finished bounded helpers also keep inspectable messages and tool calls until
the containing chat closes, rather than retaining only their final report.
This is useful baseline inspection, not another feature requiring confirmation.
Load child detail when requested instead of copying full nested histories into
ordinary tab updates. Independent agent sessions follow their own close policy;
explicit history edits still replace the removed conversation as agreed above.

### Keep both website automation and desktop control

**Decision:** Keep both capabilities; they serve different jobs.

- Website automation can operate and inspect pages in a separate browser without
  taking over the user's mouse.
- Desktop control can operate applications, browser controls outside the page,
  file dialogs and other Windows UI that website automation cannot reach.

This retains useful outcomes, not Pie's specific libraries, packaging or process
adapters. Their dependencies and implementation cost still need review. Preserve
agreement with the user before visible desktop interaction unless the request
already includes it; retaining the feature is not blanket permission to use it.

### Scheduled follow-ups survive restart and run automatically

**Decision:** Remember scheduled follow-ups across backend restarts and run them
automatically when due, without requiring the user to resume them manually.
Preserve their intended deadlines; a follow-up that became due while the backend
was stopped can run after it returns. No follow-up is dispatched while the
backend is off.

This applies to sessions that are still open. A follow-up targeting a closed
session must be cancelled/discarded rather than reopening that session. Do not
copy Pie's ability to restore a closed timer target after restart.

This is an explicit future request, such as checking a build later, not permission
to blindly replay unfinished commands or duplicate a delivery whose outcome is
uncertain. Delivery/recovery mechanics remain to be designed. Approval covers
delayed follow-ups, not automatic inheritance of Pie's broader combinations of
timer, user-input, session-finished and command-polling conditions.

## Working hypotheses, not settled commitments

### C#/.NET still needs resource and local-build evidence

The backend language is settled above. Low harness CPU/memory cost, installation,
library reuse and maintenance remain requirements. Source-bundled customisation
must work on users' own machines; prebuilt-release convenience does not establish
that the source-build workflow is acceptable. The accepted
[source-distribution direction](REPO-FOUNDATIONS.md#owner-outcome) requires matching
editable source and build instructions with distributable builds, including modified
source in custom packages. It does not require repackaging every compile or select
physical packaging, SDK/cache bundling, tooling or activation mechanics.

[Foundation research](FOUNDATION-RESEARCH.md) preserves the earlier language
comparison and local-build considerations; its shortlist is now historical.
[Focused Go/C# discovery](GO-DOTNET-DISCOVERY.md) established small streaming/tool/
control paths using real loop libraries and fake models in both languages. These
remain feasibility observations, not provider, packaging, resource or cross-platform
qualification. Further qualification should focus on the selected C#/.NET backend.

### Provisional direction: a narrow Urd-owned model/tool cycle

**Owner alignment (2026-11-04):** Prefer a small Urd-owned cycle over `IChatClient`
rather than adding full MEAI for `FunctionInvokingChatClient` now. In the bounded
comparison the library saved only **20 owned probe lines**; the owner considered
that insufficient reason for the dependency. This is **not a production LOC or
maintenance estimate**. Reconsider if real integration recreates substantial
library machinery; reuse focused libraries and Abstractions helpers where useful.

[Execution design](EXECUTION-DESIGN.md) now also exercises the real Responses
adapter + terminal guard + Urd loop + harmless tool through SQLite reload and
continuation. This closes the bounded local composition gap using a **whole-history
test envelope**, not the actual session/message-row owner. Process-kill recovery,
production streaming/cancellation and live-provider acceptance remain unqualified.
Positive provider-terminal evidence must precede tool dispatch; complete-looking
arguments, HTTP success or stream EOF alone are insufficient. Persistence/native-item
handling remains Urd-owned.

Use one execution path for both session kinds, not per-provider/session-type loop
subclasses. This provisional direction is not a final API or scaffold approval.
Earlier research's mandatory-custom-loop assumption was an assistant inference;
current direction follows comparison and owner preference, not “first-party” alone.
No Pi or complete Codex/Copilot agent runtime is introduced; package pins and
provider integrations remain separate qualifications.

### Begin with one backend process unless evidence justifies separation

A single process for the server and agent execution is the current working
default, **not a locked architecture decision**. It could remove routine
coordinator/worker IPC and cross-process ownership machinery. Ordinary
provider/tool failures must still be contained to their operation; a fatal
process failure would interrupt all active sessions. The owner accepts that shared
fatal-failure risk rather than requiring complex isolation/recovery machinery.
This accepts a trade-off, not the process topology itself.

Revisit the boundary when actual isolation, blocking work or performance needs
justify it. External commands and selected integrations can still run in their
own processes. Keeping the option open is not a reason to implement both
execution models or add speculative process-abstraction layers now.

[Backend lifecycle evidence](BACKEND-LIFECYCLE.md) exercises competing launchers,
verified attachment, stale discovery and owned-process exit on Windows. It does
not select portable locking, packaged daemon lifetime, authentication, shutdown
policy or topology; one backend remains a working default.

### Session structure and communication: owner-reviewed working sketch

**Alignment (2026-11-04), not frozen APIs:** the owner roughly accepted a small
shared session hierarchy and explicitly selected `PrimarySession` after rejecting
`ChatSession`/`ConversationSession`. `SubagentSession` was considered clear;
`SessionBase` or visibly explicit base naming was preferred. The working sketch is:

```text
abstract SessionBase
├── PrimarySession       ongoing, whether human- or agent-created
└── SubagentSession      bounded task → result
```

Both share core execution/history rules without separate engines. A composed
`SessionExecution` represents temporary active work; its name/API and exact base
scope remain provisional. Titles, compaction and pruning use shared model I/O,
not additional full-session subtypes. [Session design](SESSION-DESIGN.md) owns the
responsibility/lifetime walkthrough and acceptance proposals. It separates durable
history/assignment facts, live execution and client presentation; finished work,
explicit task results and final closure are not interchangeable.
[Disposable flow tests](SESSION-DESIGN.md#7-disposable-session-flow-qualification)
now exercise guarded admission/Stop, edit/close and retained assignment results,
including owned-child exit/reopen. They do not validate the hierarchy, bounded-child
lifecycle, HTTP/client integration or composition with the native execution fixture.

**Relationships and closure:** creator provenance/cleanup authority, bounded
caller/containing chat, assignment requester/worker and UI grouping have different
meanings. Human steering does not change session type or transfer creator authority.
Bounded children close with their containing chat. **Independent-primary closure
remains open:** the owner suggested survival or a warning before cascading closure,
but needs concrete UI before choosing. Do not encode creator as implicit lifetime
cascade or discard results still needed by another open session.

Delegated-worker clutter and slow cleanup remain the motivations. Grouping,
detachment, relative `parent` addressing and exact creation/delivery APIs are not
selected. Stable IDs independent of mutable titles are recommended in the sketch,
not a requirement to wait for title inference. Parent/child addressing cannot
replace durable assignment/results. After the initial layout deferral, the owner
selected the revisable [`Urd.Server → Urd.Core` boundary](SESSION-DESIGN.md#5-coreserver-project-boundary--selected-revisable):
Core owns session/execution/storage logic without ASP.NET; Server owns hosting,
HTTP entrypoints and composition. Fine file/folder layout and exact APIs remain
unapproved, and the coarse example does not authorize production scaffolding.

**Recovery review — delegated simplicity, not an owner-selected interface:** after
unclear examples, the owner said “whatever is simplest”. The revisable working
recommendation is to retain an uncertain effect as unknown, never automatically
replay it, and allow a fresh instruction once old owned work has actually stopped.
Do not add a reconciliation-only chat lock or wizard. This was not separately
confirmed by the owner; exact controls remain implementation work. Stop/settlement,
stale-write fences and reconciliation of ambiguous Send receipts still apply.
A new instruction does not prove the old effect succeeded or authorize repeating it.

**First-checkpoint sequencing remains engineering planning.** The owner did not
approve the proposed idle-only Send restriction for the fake-model checkpoint;
normal-use steering and follow-up input remain retained. Do not turn that test-stage
detail into another owner questionnaire or a product feature cut. Choose the bounded
checkpoint within a concrete, structurally aligned implementation plan, not by
promoting the draft or fixtures into implementation permission. See
[the scoped review](SESSION-DESIGN.md#8-resumed-inputrecovery-review--2026-11-05).

### Model-call reuse and composition: design alignment

**Alignment recorded 2026-11-03:** Reuse common model-request mechanics for agent
work and auxiliary LLM calls such as title generation and compaction. Prefer
composition: title generation uses model I/O rather than inheriting from it.
**Follow-up alignment (2026-11-04):** use the configured request-only `IChatClient`
directly for shared execution and auxiliary operations where it fits. The owner
accepted this after rejecting an unclear `ModelRequestExecutor` name; no mandatory
`ModelClient` facade replaces it. Thin provider adapters can implement the selected
interface as needed. `TitleGenerator`, `HistoryCompactor` and `CatalogPruner` are
working operation names, not approved APIs or full session types. A failed title
can fall back to a bounded excerpt of the first prompt.

Keep model invocation, the model/tool execution cycle and session state/lifetime
as distinct responsibilities. This does not require three framework layers or
make every auxiliary call a session. Reuse error/retry/fallback mechanisms while
keeping operation-specific policies explicit: a missing title is tolerable,
failed compaction must preserve usable context, and delegated fallback approval
must not silently apply to a user's selected chat model. Cancellation should not
be swallowed as a missing title; request retries must not replay completed tools.

Design only enough class structure to establish responsibilities, state ownership,
lifetimes and important failure behaviour. Use concrete flows to test the design
before specifying every method, interface or subclass. Prefer focused libraries
and .NET facilities for routine mechanics rather than recreating them beneath
custom wrappers. A small class with one useful operation can be sufficient.

Shared client use does not mean a process-wide serial executor or shared mutable
request state. Provider fidelity, caller-specific retry/fallback policy and
cancellation still need qualification; auxiliary calls do not acquire the tool
loop. The provisional cycle direction above is separate from this composition
boundary. [Execution design](EXECUTION-DESIGN.md) records evidence and remaining
gates; the older [C# discovery](GO-DOTNET-DISCOVERY.md) remains historical candidate
evidence, not a wider package selection.

### Image attachments: files with stored references

**Direction (2026-11-04):** Keep retained image bytes as files in Urd's managed
attachment directory, with references and metadata in persisted message records.
Prefer stable attachment identities mapped to relative paths rather than absolute
paths scattered through history. Retained attachments should not depend on an
original external file remaining in place. Resolve references when serving the UI,
building provider requests or supplying paths to tools; a local path alone does
not expose image content to a remote model. Clean up files only when retained
references no longer need them, including references from other sessions. The transcript database itself is
selected in the storage section below; a general large-artifact policy beyond
attachments is not selected.

### Session storage: shared SQLite

**Decision (2026-11-04):** Use **one shared SQLite database** as the store for
transcripts, session metadata and assignment/result records. The working layout
is message rows with a derived bounded-preview metadata table (the harness
family-1 shape); large image/binary artifacts remain files with stored
references per the attachment direction above, not blobs in the database.
Default durability remains WAL with `synchronous=NORMAL` as the working setting.
**Clarification of the existing process-crash contract, not a new owner requirement:**
database transactions whose commits have returned survive process crash/reopen;
live output not yet committed can be lost. This distinguishes saved database state
from provisional progress rather than promising preservation of every streamed token.
System crash or power loss is outside that guarantee: see
[SQLite's synchronous documentation](https://www.sqlite.org/pragma.html#pragma_synchronous).
No per-token fsync, numerical loss budget, attachment-file atomicity or exactly-once
external effects follow; these words do not qualify every failure mode.

**Workload and guarantees.** The store must serve click-to-visible newest-window
reads, paged navigation, model-context range reads, incremental appends and
per-batch streaming writes, suffix-replacing edits, persistent assignments
across restarts, final-closure deletion and many-session fan-out — under a
single backend process. For the process-crash/reopen boundary above, committed
database state reopens intact: a transactional record is absent or complete,
never partially applied. Live delivery alone is not a commit acknowledgement.
Edits replace history only and never roll back external effects; recovery does
not invent, reorder or duplicate records. A missing acknowledgement does not
prove that a transaction failed or that retrying it is safe.

**Rejected alternatives** (evidence in [STORAGE-RESEARCH.md](STORAGE-RESEARCH.md),
[STORAGE-EXPERIMENT.md](STORAGE-EXPERIMENT.md), the binding review and harness
run records):

- *Pure JSONL/files and segmented binary logs:* bounded reads tie with SQLite,
  but metadata, assignment, edit and reopen operations were 5–35× slower in the
  store-operations probe, and each needs a custom index plus reconciliation and
  tail-repair machinery — the JSONL prototype embedded SQLite for coordination
  anyway, at higher code cost.
- *SQLite plus external payload files as the primary layout:* two-store
  atomicity risk for ordinary transcript writes; file spill stays only for the
  attachment policy above.
- *Per-session SQLite files:* no cross-file atomicity with the coordination
  database, and session deletion becomes a filesystem operation over multiple
  files.
- *LMDB:* un-versioned (0.x) wrapper, a fresh on-disk format break, and Windows
  map-size pre-reservation, with no measured advantage.
- *RocksDB:* qualified and the closest measured rival, but a ~13 MB native
  asset plus higher dependency/LOC cost without a decisive win at Stage 1.
- *LiteDB:* attractive size and fully managed, but the stable line is unmoved
  since mid-2024, v5 offers no transaction object, and its culture-based index
  collation already caused one correctness incident in the harness.
- *Tsavorite standalone:* not qualified (no official embeddable package).
- *ORMs, event-sourcing platforms and interchangeable-backend frameworks:*
  rejected up front; Urd uses a narrow first-party storage owner.

**Consequences and revisit conditions.** The experiment programme's remaining
stages continue as *validation* evidence — the full Stage 1 matrix and Stage 2
concurrency/interruption/soak runs — not as selection. Revisit the internal
layout (rows → content blocks) only if measured large-result read behaviour
justifies a same-engine change. Revisit the engine itself only on demonstrated
pathology: unbounded WAL/vacuum cost at the largest fixture tiers, or a
concurrency failure under multiple active writers in Stage 2. Internal schema,
driver pinning and process placement remain ordinary design work, not new
decisions. Prior sections of [STORAGE-RESEARCH.md](STORAGE-RESEARCH.md) and
[STORAGE-EXPERIMENT.md](STORAGE-EXPERIMENT.md) that describe storage as
unselected are historical as of this date.

[First-slice SQLite validation](SQLITE-VALIDATION.md) subsequently exercised
bounded concurrent activity and signalled process-kill boundaries. Its
[preview follow-up](SQLITE-VALIDATION.md#preview-fix-follow-up--2026-11-04) fixes
snapshot consistency, empty-message visibility and Unicode boundaries in the
experimental adapter; separate `PageOlder`/`ReadRange` snapshot races remain.
Its conditional go supports the engine choice, not promotion of the reduced
experimental contract or closure of production lifecycle/schema gaps. These checks
do not qualify power loss, sustained contention, attachment cleanup or end-to-end
UI latency.

## Feature discovery: first pass

These observations describe inspected Pie source, not agreed Urd scope or proof
of live usage. Paths below are relative to the Pie checkout identified in the
reference section. This is not yet a complete dependency or lifecycle audit.

| Capability found in Pie | Data and responsibility worth reviewing for Urd |
| --- | --- |
| Compose, submit while busy, edit pending/earlier messages, interrupt | Conversation changes, queued input, drafts and pending attachments have different lifetimes today. Composer drafts/attachments are host runtime state; do not automatically copy that ownership into multi-client Urd. |
| Questions and attention | Inline `ask_user` cards, prompt responses, waiting markers, sounds and window attention. Pending prompts are live state today; decide the useful offscreen-agent experience before designing its transport. |
| Session organization | Tabs, pins/groups, snooze and restore, manual titles and optional model-generated titles. Snooze hides an idle chat and restores its tab later; it does not pause running work. These are separate conveniences, not one indivisible session-management feature. |
| Transcript and execution inspection | Paged history, expandable tool inputs/results, lazy child details and model-produced/model-received inspection. Exact execution detail can have very different size and retention needs from the visible conversation. |
| Guidance and discovery | Context documents, append guidance, named agents/roles and skills; main/child composition and resource precedence. Separate authored resources from the live prompt assembled from them. |
| Provider accounts and model access | API keys, supported subscription logins and local/OpenAI-compatible endpoints are distinct access needs. Much provider/auth behavior comes from `pi-ai`, behind Pie-owned management interfaces; removing Pi includes replacing those seams. API protocol families are not the same thing as login methods. Provider coverage and replacement libraries remain to be reviewed. |
| Tool/skill selection | The owner confirms successful use and explicitly retains AI-based catalog pruning with protected core entries. Checked-in defaults are not usage evidence. Pie's recovery discloses hidden tools for a wrapper rather than mutating the frozen native catalog; that implementation constraint is not automatically Urd's. |
| Result reduction and history compaction | Deterministic output rewriting with raw recall is distinct from catalog selection and from summarizing old conversation. Result reduction is enabled in checked-in defaults and excludes `read`; the mechanisms have different data and fidelity needs. |
| Files, shell, web, MCP, browser and desktop tools | Different dependency and effect boundaries: Pi-supplied core tools, Pie's warm-shell override/fallback, managed web/MCP packages, browser automation and Windows desktop integration. Dropping Pi does not make these integrations free. |
| Delegation, coordination and deferred work | Both retained delegation forms use the agent engine. Notes, assignments/results, waits, cooperative resource leases and durable wakeups are additional capabilities to review separately; wakeups currently target persisted sessions, not bounded in-memory subagents. |
| Cost and performance visibility | Session usage/estimated cost and speed indicators, plus aggregate charts. An estimate is not necessarily an invoice; incomplete observations must not silently become zero. |
| Historical analytics and queries | Automatic fresh capture includes provider settlements, executions, tools/file facets, activity spans and capability/feature observations, with separately addressed detail. Agent queries extend well beyond the small UI counters. Urd's agreed post-closure scope is much smaller: compact usage summaries, not detailed history. |
| Privacy, retention and deletion | Pie's private-session close deletes associated history/analytics; normal closure/expiry includes a separate gated policy. Source implementation is not proof that the gate is active, and neither policy has been selected for Urd. |

Pie's model-exchange inspector concerns a tool call: provider-normalized name
and arguments before execution, and the final tool content confirmed consumed
by a successful provider response. It is not an original-HTTP-byte capture or a
complete model-request log. Keep this session-debugging capability distinct from
historical analytics and from ordinary formatted tool cards when reviewing value.

### Analytics investigation: observed value and limits

A separate read-only investigation examined the owner's canonical Pie analytics,
followed by independent review and a small local diagnostic check. Aggregate
figures came from one snapshot of a multi-week dataset; missing usage/outcomes
and unclassified test/development activity limit the conclusions.

- Counts of tool outcomes, model calls by purpose and measured retry waits gave
  leads for investigating failures, delegated workload and pauses. They did not
  establish waste, model quality, successful task delivery or a concrete fix.
- File-change counts represented attempted, unverified edits. Queried pruning
  and initial-context observations lacked size-effect measurements; they did
  not establish whether those features helped. That is missing evidence, not
  proof that the features are useless.
- A deliberately small sample of 12 retained failed shell-command results was
  checked. Local text-pattern classification found a few ordinary file/permission
  diagnostics, but established no root cause or justified improvement. It was not
  a representative sample or an evaluation of every rich-detail use.

**Outcome:** The owner chose small usage summaries after closure, without
retaining conversations or full tool outputs. This investigation did not
demonstrate a need to inherit Pie's broad file/activity tracking, permanent
detailed payloads or general historical-query machinery. That is not proof that
all richer analysis is useless. Keep open-session tool inspection separate; no
data engine or detailed capture protocol is selected by these findings.

The report, queries and sanitized evidence remain in the owned local investigation
directory `%TEMP%\urd-analytics-investigation-jLnBHj\`; private runtime data and
raw tool bodies have not been copied into this repository. These are bounded
research observations, not production acceptance or a full analytics value audit.

### Source anchors

- Interaction and inspection: `application/lib/protocol/webview.ts`,
  `application/frontend/composer/`, `application/frontend/transcript/`,
  `application/frontend/session-tabs/`,
  `application/backend/composition/completion-attention.ts`, and
  `docs/contracts/STATE_CONTRACT.md`.
- Questions, titles and deferred work: `harness/tools/ask-user/`,
  `docs/contracts/SESSION-TITLES.md`, `docs/contracts/DEFERRED-TRIGGERS.md`, and
  `docs/contracts/AGENT-SESSION-CONTROL.md`.
- Guidance/tools: `harness/agent-instructions/README.md`,
  `harness/tools/README.md`, `harness/tools/catalog/index.ts`,
  `harness/tools/warm-bash/README.md`, and
  `harness/tools/package-integrations/web-access/README.md`,
  `docs/operations/PLAYWRIGHT.md`, and `docs/operations/COMPUTER-USE.md`.
- Provider/auth boundaries: `harness/model-providers/management/auth-port.ts`,
  `harness/model-providers/management/pi-auth.ts`,
  `harness/model-providers/management/contracts.ts`, `models.yaml`, and
  `harness/pi/packages/ai/src/utils/oauth/index.ts`.
- Context controls: `harness/tool-and-skill-selection/README.md`,
  `harness/tool-and-skill-selection/settings/config.ts`,
  `harness/tool-and-skill-selection/lifecycle/register.ts`,
  `harness/agent-instructions/skill-selection/skill-policy.ts`,
  `harness/tools/selection/tool-policy.ts`,
  `harness/tools/invoke-tool/recovery.ts`,
  `harness/tools/result-processing/README.md`, and `settings.defaults.json`.
- Analytics: `docs/contracts/ANALYTICS_IMPLEMENTATION_CONTRACT.md`,
  `harness/agent-instructions/skills/query-analytics/SKILL.md`,
  `application/frontend/analytics/`, and
  `application/backend/analytics-views/`.

### Consequences for the review, not implementation decisions

- Do not infer a feature is unused because it is optional or disabled by default.
  Ask about the outcome the owner values after establishing what the source does.
- Separate capability from machinery. Useful historical queries do not require
  copying Pie's recorder/helper/process topology; useful shell execution does
  not automatically require its warm pool and SDK fallback.
- Keep the three context controls distinct. AI-based catalog pruning is now
  explicitly retained; simplify its implementation without silently replacing it
  with discovery alone or conflating it with result reduction and compaction.
- Inspect actual feature data needs before selecting storage: prompts/drafts,
  assignments, raw model/tool detail, historical accounting and private deletion
  do not all have the same owner or lifetime.
- Provider adapters/admission, startup/delivery, full cross-client lifecycles and
  detailed data volumes remain incomplete discovery areas.

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
