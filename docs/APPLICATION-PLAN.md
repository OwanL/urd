# Application structure — working baseline

2026-11-05. **Owner-aligned rough structure, deliberately open to correction during
implementation.** The owner accepts the component-led tree as a decent starting point
and requests other centralized repository areas, particularly `docs/`.

This is the current layout reference. It replaces the rejected project-first and
`systems/` proposals; [structure research](STRUCTURE-RESEARCH.md) retains their lessons
and source evidence. Acceptance of this starting structure does not freeze helper
names, mandate empty scaffolding, complete the TBD areas or authorize production code.
The owner prefers flat documentation, optionally `docs/scratch/`, and drops
`experiments/` from the target layout. Use `scripts/` rather than the vague
`development/` label for executable automation. The shared-assets home remains
conditional. The owner subsequently authorized moving the experimental tree to
machine-local scratch; [the archive note](../README.md#experimental-source-archive)
records the verified relocation. Documentation has not yet been reorganized.

## Working layout

```text
urd/
├─ README.md                          concise project entry point
├─ LICENSE
├─ docs/                              flat documentation, with clear filenames
│  ├─ README.md                       documentation index
│  └─ scratch/                        optional provisional project drafts
├─ guidance/                          editable runtime guidance, not project docs
│  ├─ prompts/
│  ├─ agents/
│  └─ skills/
├─ source/
│  ├─ backend/                        other application responsibilities TBD
│  │  ├─ core/
│  │  │  ├─ sessions/                 registry, lifecycle and session commands
│  │  │  │  ├─ messaging/             messages between sessions
│  │  │  │  ├─ assignments/           delegated work, results and waits
│  │  │  │  └─ wakeups/               timer/event/condition-triggered input
│  │  │  ├─ execution/                model/tool loop, cancellation and settlement
│  │  │  ├─ context/                  assembly, selection, compaction and recall
│  │  │  ├─ guidance-loading/         scoped discovery and reading
│  │  │  ├─ tools/                    definitions, implementations and invocation
│  │  │  ├─ providers/                models, accounts, adapters and request policy
│  │  │  ├─ mcp/                      connections, discovery and protocol integration
│  │  │  ├─ storage/                  durable harness records and attachments
│  │  │  └─ usage/                    usage and cost accounting
│  │  └─ Urd.Core.csproj               ASP.NET-free backend library
│  ├─ ui/                             internal structure TBD
│  ├─ hosting/                        internal structure TBD
│  │  └─ server/
│  │     └─ Urd.Server.csproj
│  ├─ tests/                          broader test organization TBD
│  └─ Urd.slnx
├─ scripts/                           setup/check/build/package scripts; detail TBD
├─ assets/                            only if shared branding/icons actually need a home
├─ data/                              persistent copy data
├─ builds/                            generated distribution outputs
└─ .tools/                            acquired development tools
```

The runtime/source grouping is the accepted rough starting point, not a required
folder per small responsibility. A few files can remain together until a subdivision
helps. The Core project includes its normal backend subtree; Server references Core.
No explicit source-inclusion workaround, project per module or generic framework is
required. One backend test project can cover multiple owners; UI tests may stay beside UI.

## Centralized repository areas

- **`docs/`** stays flat: architecture, setup, usage, research and plans use clear
  filenames rather than category directories. Keep the root README short and link
  to the docs index. Document status/content distinguishes decisions, proposals and
  evidence; a file's location does not promote its authority. Component READMEs may
  remain beside their code.
- **`docs/scratch/`** is optional for provisional project drafts worth sharing.
  It is not a dumping ground for generated logs, downloads or disposable experiments;
  temporary agent working notes/artifacts still belong in owned OS-temp locations.
- **`guidance/`** contains editable prompts, named-agent definitions and skills used
  by the harness. `core/guidance-loading/` is the corresponding loader code.
  This does not absorb existing user/project guidance or settle loading/reload timing.
- **`scripts/`** contains executable setup/build/check/distribution scripts. Their
  documentation lives as ordinary files directly under `docs/`; no second automation
  directory or general-purpose development bucket is needed.
- **`assets/`** is conditional: shared branding/icons used by multiple surfaces or
  packaging can live here. UI-only assets stay with UI; managed session attachments
  are persistent data, not repository assets. Do not create an empty directory now.
- **`source/tests/`** is the baseline's centralized home for cross-component backend tests,
  inside the production build-policy scope. A second root test directory is unnecessary.
- **No `experiments/` in the target layout.** The existing tree has been preserved
  outside the checkout in machine-local scratch with verified file hashes and Git
  provenance. Findings remain documented; historical paths and reproduction commands
  need their archived context. Experimental projects do not belong in `docs/scratch/`.

Borrow Pie's separation of documentation, automation and runtime guidance, not its
full directory hierarchy. No changes to Pie are involved.

Existing root design/research reports should move under `docs/` as a coordinated,
link-checked documentation reorganization. Preserve report evidence and authority;
update references together rather than leaving competing copies or stale entry points.
This update records that organization, not a claim that the migration already happened.

## Responsibilities survive directory changes

- The broad harness groups agent machinery; `execution` names the narrow model/tool
  loop. Shared provider accounts and MCP connections do not become run-owned objects.
- Model-context selection is not tool permission. Compaction does not delete original
  transcripts. Session tools enter ordinary guarded commands rather than owning a
  second session/assignment state machine.
- Messaging, assignments and wakeups have distinct lifetimes. Assignment acceptance,
  worker input and wakeup dispatch retain the selected guarded admission and atomic
  store boundaries. Ordinary mid-response input is not replaced with idle-only Send.
- Harness storage owns harness-specific durable records; shared database mechanics
  can be factored later if real non-harness consumers need them. Retained usage/cost
  facts belong to the harness; UI presents them. Moving folders changes neither
  retention nor the lifetime of accepted work.
- User-question waits belong to their originating operation/execution, not a browser
  tab. Provider-login operations are not agent executions. Settings meaning and
  validation stay with their domain owners, rather than a global policy manager.

## Deliberately unfinished

UI, hosting, other application-level backend responsibilities, application settings,
platform integration, and development/delivery internals remain TBD where not worked
out. Their absence from a detailed tree is not a scope cut or a decision to put all
backend behavior in the harness. Existing technology and behavior decisions still stand.

The [architecture](ARCHITECTURE.md), [session](SESSION-DESIGN.md),
[execution](EXECUTION-DESIGN.md), [discovery](DISCOVERY-DESIGN.md),
[browser](BROWSER-DESIGN.md) and [build](REPO-FOUNDATIONS.md) records retain authority
for their scoped outcomes. Exact tool pins/acquisition mechanics, full build baseline,
transport/browser-gate details, remote trust, credential portability, physical packaging
and installed-release activation/data policy remain open where those records say so.
Matching source still accompanies distributables; custom-copy data remains separate
from generated/cleaned/packaged outputs. This tree does not qualify those workflows.

## Initial implementation: owner design review first

The owner subsequently requests a stronger initial design gate: agents propose the
important structures for review before implementing them, including class members and
method signatures where useful. Present bite-sized proposals case by case, normally
one class or cohesive operation: relevant code/members inline, a short reason and the
architectural choice at hand. Wait for the owner's response before the next bite.
Review wider lifetimes, state, dependencies and transaction boundaries internally.
The rough layout remains accepted; exact new APIs remain proposals until reviewed.

The [Core draft](scratch/initial-core-design.md),
[UI/hosting draft](scratch/initial-ui-hosting-design.md) and
[build/startup draft](scratch/initial-build-setup-design.md) are internal integration
references, not required owner reading or whole-document approval requests.
Each workstream must obtain independent
agent review and fix supported issues before the coordinator brings its concrete
code/class/method proposal to the owner. The owner reviews architectural shape and
code, not engineering guidelines or process documents. [Code-shape guidance](code-shape-guidelines.md)
is maintained by the agents/coordinator; its size signals are not hard limits.
[AGENTS.md](../AGENTS.md) records this workflow. Peer-review acceptance is not owner approval.

An approved bite permits only its presented scope, not the rest of a draft, unrelated features or external
actions. Once the initial base is accepted, follow its conventions and use implementation
evidence to improve routine names/helpers. Raise substantive responsibility, lifetime,
public-contract, concurrency or persistence changes before treating them as settled;
do not turn every private method into a repeated approval question. No application
code is authorized merely by writing these proposals. The owner subsequently authorized
clear-cut naming/readability corrections without per-name approval, including existing
public members with unchanged meaning. This does not approve new features or resolve
ambiguous state/ownership semantics; those remain substantive decisions.

### Near-term sequence — each new operation still needs its own approval

The identity foundation, first fake-text Core Send/execution increment and approved bounded
read surface are implemented. Prioritize a working integrated path, not more broad technology
comparisons or a larger complete specification:
1. The Core checkpoint now commits input decisions/run association, streams through a
   borrowed client and saves terminal state after cleanup. Two chats share a client without
   serializing model work; retained acceptance does not replay after normal reopen.
   Coherent snapshots and full-content pages now expose committed state without replay;
   final output remains distinct from settled work. The owner selected snapshot polling
   for the first browser increment; browser wiring itself is not implemented yet.
   This staging increment refuses distinct busy input rather than queuing it. The approved
   `SessionManager.ShutdownSessionsAsync` now fences and drains owned work without disposing
   borrowed resources; startup recovery, public addressed Stop and push subscriptions remain pending.
   See the [read review map](backend-development.md#read-contract-and-incremental-review-order) before expanding it.
   The approved [health-only Server startup slice](server-development.md) now supplies real
   loopback listening, exclusive data ownership and orderly shutdown. Chat endpoints and UI
   remain separate unapproved bites; this is not the integrated browser milestone.
   Keep independent Core/storage and UI/hosting/build work parallel with disjoint ownership;
   routine dependency wiring is not another approval gate. This is not whole-Core approval.
2. Order the necessary small Core and hosting/UI bites toward one built browser → Core →
   SQLite fake-model response, then extend that same path to two clients/chats, input while
   busy, addressed Stop/settlement and reopen without replay. Do not add a parallel demo
   implementation or defer integration until every proposed component is finished.
3. Exercise tiny output chunks and a slow observer on that path: measure commit/view/read
   counts, guard occupancy, click-to-visible content and memory under background work.
   Include one source-edit/build/reload check; use observations to choose practical budgets.
4. As transcript/context ownership arrives, port decisive already-sent-result fidelity
   and positive-terminal regressions into ordinary production-path tests. Qualify those
   before calling a provider path usable; live-account use remains separately authorized.
