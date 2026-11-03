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

**Still open:** .NET version, provider and loop libraries,
loop ownership, storage, process topology, UI technology and desktop host.
The language choice does not qualify performance, provider coverage, packaging
or cross-platform behaviour; investigate those within the chosen backend direction.

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
locked choice. .NET version, endpoint/event protocol, authentication, packaging
and process topology remain open. Agent work must retain its own lifetime rather
than being cancelled by an HTTP request or client disconnect.

### MCP client library and revisiting library choices

**Decision (2026-11-04):** Use the official C# MCP SDK's
`ModelContextProtocol.Core` for MCP client protocol and transport mechanics.
[The research](MCP-LIBRARY-RESEARCH.md) found no evidence-based blocker; qualify
required servers and transports during implementation. Its MEAI abstractions
dependency does not select `IChatClient`, a model/tool loop or model exposure policy.
Urd retains connection lifetime, credentials, permissions and discovery/exposure
responsibilities. The inspected package version is not a locked dependency version.

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
part of the destination; client implementation technologies remain undecided.

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

**Status:** The cost condition has not been demonstrated. Do not present
independent navigation as unconditional scope or invent a cost estimate. When
implementing, compare the minimal designs without building a configurable
framework simply to support both.

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
This does not require separate agent engines, storage implementations or class
hierarchies. Sharing their underlying representation remains a proposal, and
exact cancellation, retention and ownership rules still need review except for
the creator-cleanup boundary below.

**Creator cleanup after human steering:** Keep Pie's behaviour. Direct human
steering does not remove the creator agent's authority to close its worker; an
idle human-steered session remains eligible for parent cleanup. Retain the
running-session skip by default and instructions to collect results and check
human use before closing. Do not add a hard takeover/protection rule merely
because the user has spoken in the child chat. Urd's final-closure policy still
applies; this does not inherit Pie's normal-session history retention.

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
cache reuse needs provider evidence. No wrapper, native-discovery protocol or
model library is selected here, and cached descriptions must not override current
execution permissions.

**Follow-up alignment (2026-11-03):** The [cache investigation](CACHE-RESEARCH.md)
supports the original concern: changing early tool definitions generally loses
prefix reuse across the later conversation, not necessarily the earlier unchanged
prefix or the old cache entry itself. Urd-specific savings remain unmeasured.
Continue dynamic tool discovery design and use its requirements to inform
model-layer selection.

The owner prefers native calls over a generic invocation wrapper and considers
OpenAI/Anthropic-specific handling reasonable. Explore one Urd discovery/execution
model with provider-specific exposure: native appended definitions where supported,
and an explicit fallback elsewhere. Ordinary tool-list updates with cache cost and
a stable wrapper remain alternatives, not selected policies. Preserve the retained
AI-based catalog pruning and protected core tools; discovery complements them.
Compare adapter code, model reliability and cache impact, including tool-capable
local models. [Model-interface research](MODEL-INTERFACE-RESEARCH.md) records current
SDK/adapter gaps; neither `IChatClient` nor its optional loop is selected yet.

**Owner decision: defer model-interface selection (2026-11-03).** Keep
`IChatClient` as a candidate, not an adopted dependency or mandatory design boundary.
The [discovery design investigation](DISCOVERY-DESIGN.md) found adapter and restart
representation gaps, not a fundamental rejection. Revisit once concrete execution,
discovery and persistence needs make the integration cost clearer. Continue designing
those responsibilities without assuming MEAI's types are Urd's domain or storage model.

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
that the source-build workflow is acceptable.

[Foundation research](FOUNDATION-RESEARCH.md) preserves the earlier language
comparison and local-build considerations; its shortlist is now historical.
[Focused Go/C# discovery](GO-DOTNET-DISCOVERY.md) established small streaming/tool/
control paths using real loop libraries and fake models in both languages. These
remain feasibility observations, not provider, packaging, resource or cross-platform
qualification. Further qualification should focus on the selected C#/.NET backend.

### Model/tool loop ownership is open

The provider research initially treated Urd owning the model → tool execution →
model cycle as a requirement. **That was an assistant inference, not an agreed
owner decision, and has been withdrawn.** A first-party harness with no Pi
dependency does not by itself settle who implements or runs this cycle.

Compare a Urd-controlled loop with a reusable loop library against the required
product behaviour. Consider actual control, data/lifecycle ownership, provider
coverage, first-party adapter/workaround code and maintenance, rather than treating
either custom code or an SDK as inherently simpler. Neither is selected or
automatically qualified.

The owner has excluded complete agent runtimes such as Copilot CLI and Codex
app-server as Urd's foundation. Build Urd's lightweight harness for its own needs,
not a wrapper around another agent application. This does not require hand-writing
shared model-client or loop mechanics; focused libraries remain eligible. The
no-Pi dependency direction, with case-by-case snippet reuse, remains unchanged.

The earlier TypeScript/Vercel AI SDK comparison proposal is superseded by the
C# backend decision. Compare reusable loop libraries and a Urd-controlled loop
within that direction, preferring the approach that meets the behaviour with
less maintained code. [Provider research](PROVIDER-RESEARCH.md) preserves the
earlier evidence and limits; its complete-runtime comparisons are historical
research, not current candidates. No loop library or ownership model is selected.

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

### Session structure and communication: brainstorming checkpoint

**Discussion recorded 2026-11-03; no topology or closure policy selected.** The
owner sees ordinary chats, bounded subagents and delegated sessions as variations
on a shared session concept. Pie already shares its agent engine; simplification
must address actual lifecycle, persistence or inspection complexity, not assume
Pie has unrelated engines.

**Owner's pain points:** Delegated sessions crowd personal session navigation,
and agents can take too long to close workers after finishing with them. Consider
presentation and predictable cleanup separately from shared execution machinery.
The owner is exploring parent/child structure and simpler relative communication
instead of much of the existing delegated-session communication indexing.

Ideas discussed include returning a child's stable ID from creation, addressing
children by that ID and resolving a special `parent` address. Exact tool names,
address syntax and indexing are not selected. A single-parent structure is a tree
(or forest); a DAG allows multiple parents and needs different addressing and
ownership rules. Cyclic ownership would complicate cleanup. Creation provenance,
lifetime ownership, collaboration and UI grouping need not be the same relation.

The owner initially proposed waiting for title generation before returning child
identity. The assistant recommended allocating identity independently of mutable
titles and waiting for registration/addressability instead; title generation can
fail or finish later. Keep this distinction in the next lifecycle design review,
not as an already specified creation protocol.

The owner's tentative preference is to keep bounded subagents as simple task-in/
result-out helpers; sharing session representation does not require giving them
delegated sessions' ongoing messaging interface. Parent/child addressing also does not replace durable assignment/result
facts or define delivery guarantees. Those facts must still survive restart as
already agreed.

Assistant proposals, not decisions: a flat registry with explicit ownership and
separate collaboration links; delegated workers grouped under their originating
chat, potentially detachable; finished assignments distinguished from closed
workers. Whether parent closure ends running children, whether workers can have
multiple lifetime owners, and how cross-family collaboration works remain open.
Existing creator-cleanup authority after human steering remains agreed; speaking
to a worker does not by itself establish a new ownership rule.

### Model-call reuse and composition: design alignment

**Alignment recorded 2026-11-03:** Reuse common model-request mechanics for agent
work and auxiliary LLM calls such as title generation and compaction. Prefer
composition: title generation uses model I/O rather than inheriting from it.
`ModelIo` and `TitleGeneration` are illustrative names, not approved class APIs.
The caller should be able to express generation and a simple fallback clearly;
a failed title can fall back to a bounded excerpt of the first prompt.

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

ASP.NET Core hosting and the official MCP client SDK are now selected above.
The existing [C# discovery](GO-DOTNET-DISCOVERY.md) supplies remaining candidates,
not selected dependencies: `Microsoft.Extensions.AI` and provider adapters for
model I/O, its optional `FunctionInvokingChatClient` for loop reuse, and
Playwright .NET. Evaluate the owned integration
code and behaviour each saves. Provider access, library compatibility and
packaging still need qualification; neither a full agent framework nor a custom
loop is selected by this alignment.

### Image attachments: files with stored references

**Direction (2026-11-04):** Keep retained image bytes as files in Urd's managed
attachment directory, with references and metadata in persisted message records.
Prefer stable attachment identities mapped to relative paths rather than absolute
paths scattered through history. Retained attachments should not depend on an
original external file remaining in place. Resolve references when serving the UI,
building provider requests or supplying paths to tools; a local path alone does
not expose image content to a remote model. Clean up files only when retained
references no longer need them, including references from other sessions.
This selects neither the transcript database nor a general large-artifact policy.

### Storage technology requires comparative investigation

**No storage technology has been selected.** SQLite plus file-backed large
payloads was suggested, but the owner explicitly deferred the decision pending
research. Plain files/JSONL, SQLite and any hybrid must be evaluated rather than
adopted because they are familiar or sound attractive in isolation. Session
click-to-content latency is a primary criterion in this investigation.

The source comparison and local experiments are recorded in
[STORAGE-RESEARCH.md](STORAGE-RESEARCH.md); findings there are evidence, not an
architecture commitment. After reviewing them, the owner considers SQLite
promising but explicitly does **not** select it even as the working storage
direction yet. Broader discovery of features, data needs and system boundaries
must precede that commitment; narrow benchmarks are not sufficient.

This is a fundamental, costly-to-reverse decision. The confirmed references are
upstream Pi's coding agent, **Pi Durable** (the first-party upstream package,
not a third-party Pi fork), **DeepSeek Harness**, Codex and Claude Code.
Distinguish inspectable source, documented behaviour and unsupported inference.

Compare streaming writes, history loading, edits/truncation, concurrent work,
assignment/result persistence, crash consistency, large payloads and indexing.
Include memory/startup costs, dependencies, distribution burden, implementation
LOC and ongoing maintenance. Performance conclusions need representative
workloads; neither another project's choice nor an isolated microbenchmark
establishes what is best for Urd. The research must inform a decision, not
silently turn a candidate into the architecture.

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
