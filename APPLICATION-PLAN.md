# Application structure — working baseline

2026-11-05. **Owner-aligned rough structure, deliberately open to correction during
implementation.** The owner accepts the component-led tree as a decent starting point
and requests other centralized repository areas, particularly `docs/`.

This is the current layout reference. It replaces the rejected project-first and
`systems/` proposals; [structure research](STRUCTURE-RESEARCH.md) retains their lessons
and source evidence. Acceptance of this starting structure does not freeze helper
names, mandate empty scaffolding, complete the TBD areas or authorize production code.
The documentation categories and conditional shared-assets home below are planning
recommendations; existing files have not been relocated as part of this update.

## Working layout

```text
urd/
├─ README.md                          concise project entry point
├─ LICENSE
├─ docs/                              long-lived project documentation
│  ├─ README.md                       documentation index
│  ├─ architecture/                   current decisions, boundaries and design
│  ├─ development/                    setup, building, testing and contributing
│  ├─ user-guide/                     using and customizing Urd
│  ├─ research/                       investigations, comparisons and evidence
│  └─ plans/                          working plans and unfinished proposals
├─ guidance/                          editable runtime guidance, not project docs
│  ├─ prompts/
│  ├─ agents/
│  └─ skills/
├─ source/
│  ├─ backend/                        other application responsibilities TBD
│  │  ├─ harness/
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
├─ development/                       setup/check/build/package automation; detail TBD
├─ assets/                            only if shared branding/icons actually need a home
├─ experiments/                       existing executable research
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

- **`docs/`** is the main human/developer documentation home. Keep the root README
  short and link to its index. Current architecture, proposed plans and research
  evidence must retain their distinct authority; moving a report does not turn it
  into an approved design. Component READMEs can stay beside the code they explain.
- **`guidance/`** contains editable prompts, named-agent definitions and skills used
  by the harness. `harness/guidance-loading/` is the corresponding loader code.
  This does not absorb existing user/project guidance or settle loading/reload timing.
- **`development/`** contains executable setup/build/check/distribution tooling;
  **`docs/development/`** explains how to use and maintain it. Do not create a second
  competing root `scripts/` home just to mirror Pie's directory names.
- **`assets/`** is conditional: shared branding/icons used by multiple surfaces or
  packaging can live here. UI-only assets stay with UI; managed session attachments
  are persistent data, not repository assets. Do not create an empty directory now.
- **`source/tests/`** is the baseline's centralized home for cross-component backend tests,
  inside the production build-policy scope. A second root test directory is unnecessary.
- **`experiments/`** keeps executable research and its local READMEs. Long-lived
  findings belong in `docs/research/`, without moving experimental source into docs.

Pie provides a useful reference: its tracked root separates documentation, scripts
and tests, and its docs distinguish architecture, contracts, operations, plans and
research. Borrow the separation of purposes, not every directory or another tooling
system. No changes to Pie are involved.

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

Use implementation evidence to improve names, nesting and small helper boundaries;
do not hold another broad layout vote for routine corrections. Update the relevant
docs with substantive changes, and raise material changes to agreed responsibilities,
runtime behavior or dependency direction before treating them as settled. Future
implementation still needs an explicit bounded task; this update is documentation only.
