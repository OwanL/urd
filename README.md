# Urd

Urd is a ground-up reimplementation of Pie's settled product direction. Pie is
already a good product; Urd exists to make that direction maintainable,
lightweight and publishable over the long term, not to redesign what Pie is.

Urd is an agent workspace with software development as its first focus, without
excluding general-purpose agent work.

## Status and authority

This document records agreed direction and its rationale. It is a foundation
for future design and development, not a detailed implementation specification.
Unresolved choices and proposals are identified explicitly; they are not
approval to implement a particular design.

Update this document when direction changes, preserving the reasons behind
meaningful trade-offs. Do not reconstruct project intent from isolated remarks
or treat an old proposal as a current decision.

## Why this repository exists

The project owner's reasons for a fresh implementation are:

- **Accumulated complexity.** Pie's codebase became too large and complicated,
  with outdated, abandoned or superseded systems and remnants obscuring the
  current direction.
- **Outgrowing Pi.** Pi no longer fully aligns with what Pie has become. Adapting
  around its runtime and assumptions creates coupling Urd should not inherit.
- **Publication and customisation overhead.** Publishing through the VS Code
  Marketplace while maintaining deep customisation of guidance, UI,
  extensions, hooks and tools has become too complicated. Delivering editable
  source is preferable to maintaining bespoke systems for every kind of change.
- **A settled product deserves a coherent foundation.** The aim is to rebuild
  what Pie offers as first-class, deliberately designed systems, rather than
  perpetuate the path by which Pie arrived there.

These are motivations, not a claim that a rewrite automatically makes the
product simpler. Urd must avoid recreating the same machinery under new names.

## What carries forward from Pie

**Pie's core systems, guidance and settled workflows are the product baseline,
not a mandatory parity checklist.** Urd is not an unrelated minimal agent
product, but individual features may be questioned, simplified, replaced or
removed when they do not earn their complexity and maintenance cost. Unused
features are candidates for review, not automatic requirements.

Current Pie responsibilities include:

- Agent sessions, concurrent work, history, persistence and recovery.
- Delegation, agent/session coordination and deferred work.
- Agent guidance, named agents, skills, tools, discovery and external tool
  integration such as MCP.
- Provider/model management and tool, skill and context selection/pruning.
- The shared application backend and session-oriented user interfaces.
- Local usage/accounting analytics and user configuration.

This list is an orientation, not an exhaustive parity checklist or a selection
of Urd implementations. Inspect the relevant current Pie behaviour, owning
contracts and guidance before deciding each area's future. Distinguish the
value of a capability from the cost of its current implementation: unnecessary
machinery may be replaceable without losing the useful behaviour. Record
intentional feature removals and their rationale rather than losing them as
incidental implementation cuts. Obsolete systems and abandoned approaches are
not part of the desired inheritance.

Software development is the initial focus, not a requirement to make all agent
work inherently about code. There is no separate general-purpose product to
build alongside the development-focused one.

## Decisions and rationale

### Keeping lines of code down is a strict requirement

**Urd must keep its codebase small. This is a strict requirement, not an
optional preference.** Pie grew too large in both code and complexity; Urd must
not repeat that outcome. Features, abstractions, dependencies and supporting
infrastructure must justify the code and maintenance burden they introduce.

Prefer the smallest clear implementation that meets the agreed behaviour.
Reduce machinery, duplication and unnecessary generality rather than compressing
formatting or hiding complexity. Code-size costs must inform design decisions,
not be considered only after a large system has been built. No numerical cap
for the entire repository has been selected.

### Responsiveness and scale are fundamental

The UI must remain responsive throughout the application, including minimal
delay between clicking a session tab and seeing its contents. Substantial agent
fan-out must not make interaction sluggish. Support as much work as is practical
without treating speculative workload examples as hard limits. Concrete sizing
scenarios and performance investigations belong in [ARCHITECTURE.md](ARCHITECTURE.md).

### Own a first-party harness; remove Pi

**Pi will not be a dependency or runtime coupling.** Urd's harness will be
mostly bespoke, lightweight and aligned with what Pie offers. Core capabilities
belong in its design rather than being bolted onto another application's
extension seams.

Prefer purpose-built systems over a general framework for arbitrary harnesses.
Less generalisation and less customisation infrastructure are intentional;
deep customisability comes from access to the source instead.

Ideas and useful logic may be borrowed from other harnesses. **Selected snippets
of Pi logic may be considered case by case; this is not approval to adopt a Pi
package.** First-party ownership does not require inventing every algorithm or
rejecting focused libraries. It means Urd owns its architecture and behaviour. Ownership of the
model/tool execution cycle remains open for comparison and owner alignment;
"first-party" must not be used as shorthand for a mandatory custom loop.
Specific language, library and framework choices remain open.

### Bundle source with published builds

Users should have the source needed to change the application, including its
guidance, UI and runtime capabilities. Do not recreate an elaborate extension
or override platform merely to make those source changes possible.

**Editing source, then building and reloading, is an acceptable normal workflow.**
Make the developer and user experience as smooth as is feasible without adding
substantial complexity. Consider hot reload for both UI and backend where it
can be provided simply; it is not a requirement that justifies a large reload
system. Build and reload is a valid fallback, not a design failure.

**Users own their modifications.** They may maintain a fork, contribute a pull
request, keep local edits or do nothing. Git use, publishing a fork and upstream
contribution are not requirements. Urd is not responsible for preserving or
merging arbitrary source edits through upgrades. The exact packaging, editing
location and update mechanics still need design.

### Deliver the browser client first

**The browser is the first usable Urd interface.** Continue developing Urd from
Pie until Urd is ready for normal use; there is no need to rush self-hosted
development or cut essential behaviour merely to switch tools early.

Standalone Windows and VS Code clients remain planned. This orders delivery,
not the eventual product scope, and does not select a language, UI toolkit,
provider library or storage technology.

### Share the backend across user interfaces

**The backend does the work; the UI is an interface into it.** Browser, desktop
and VS Code clients access the same backend and agent sessions rather than
operating separate agent runtimes for each surface.

Native desktop and VS Code clients should connect to an existing backend or
start it when absent. Closing a UI leaves the backend running; stopping the
backend is an explicit user action, not a consequence of a client disconnecting.
This permits switching interfaces without tying ongoing work to whichever UI
happened to start it.

Users need a straightforward way to know whether Urd is running and how to
stop it. A **per-user background app with a system-tray icon**, similar to
Ollama, is the proposed approach. It is not yet a commitment to an OS service,
a particular tray toolkit, automatic startup at login or detailed shutdown
behaviour. Background operation must not become a reason to build a general
process-management platform.

### Design for multiple surfaces; deliver incrementally

The long-term destinations are:

- **Web:** browser access through localhost, LAN or Tailscale.
- **Native desktop application.**
- **VS Code sidebar.**

These boundaries must be considered from the outset, but all surfaces need not
ship initially. Browser delivery comes first; the subsequent order of desktop
and VS Code delivery remains open.
**A TUI is not required and is outside the intended scope.** This does not
prohibit ordinary launch, build or maintenance commands.

**Windows first.** Windows is the first supported operating system, not a
permanent Windows-only product constraint. Other OS commitments and timing
remain open.

### Start fresh; do not carry data compatibility

**Urd does not need compatibility with existing Pie sessions or settings, and
has no migration requirement.** It may define its own data model without a
legacy reader, compatibility layer or importer. Product continuity does not
mean storage-format continuity. Existing Pie data is not Urd's to modify.

## Guidance for future development

- **Inspect before asking the owner to re-specify Pie.** Its settled core
  systems and guidance are the starting point. Use concrete evidence and
  scenarios to question a feature's value; do not ask the owner to reconstruct
  behaviour that can be understood from the repository. Lack of evidence of
  use is not proof that a feature is unused.
- **Reserve owner questions for real trade-offs.** Preserve clearly useful,
  settled behaviour without requesting feature-by-feature reapproval. Research
  routine technical choices independently; ask only about consequential
  ambiguity, conflicting requirements or meaningful product changes. Explain
  the practical trade-off in plain terms and recommend an answer.
- **Distinguish product continuity from implementation inheritance.** A fresh
  implementation is required; retaining obsolete architecture is not. Conversely,
  removing a core capability is a product decision, not an incidental MVP cut.
- **Keep systems first-class and purpose-built.** Evaluate abstractions against
  Urd's actual requirements, not hypothetical consumers or arbitrary extension
  scenarios. Source access is the escape hatch for deep customisation.
- **Stage the work without silently shrinking the destination.** Delivery
  milestones may be incremental; changes to the destination require an
  explicit feature decision, not an accidental omission.
- **Demonstrate improvement rather than assuming it.** Preserve useful behaviour
  while making the code easier to understand, change, build and publish. Keeping
  LOC down is mandatory, but a lower count alone does not prove correctness or
  maintainability. Do not justify code growth merely by calling it architecture.
- **Keep personal material separate from product defaults.** Pie's maintainer
  settings are not automatically Urd defaults. Do not import unrelated local
  configuration or live data as part of the rebuild.
- **Respect the distinction between decisions and proposals.** This document
  does not silently select technologies, protocols or storage formats.

The repository currently contains a GNU AGPL v3 license; see [LICENSE](LICENSE).

## Reference material

Reference inspection used Pie commit
`cc82abb0f317f31341d3a6f12aa1545f6be1fbea`. Useful paths **in the Pie repository**:

- `docs/architecture/ARCHITECTURE.md`: responsibilities and ownership.
- `docs/contracts/`: behavioural guarantees for the relevant system.
- `harness/agent-instructions/`: agent guidance and resource conventions.
- `harness/tools/README.md`: tool responsibilities and integration boundaries.
- `docs/plans/PIE-PRODUCTIZATION.md`: product and delivery decisions, with
  implementation and qualification status distinguished.

The reference commit identifies the inspection, not an agreed frozen parity
baseline. Historical plans, superseded designs and unqualified work must not be
mistaken for settled, working behaviour. Pie's implementation documents are
reference evidence, not Urd architectural contracts.

## Architecture and feature review

[ARCHITECTURE.md](ARCHITECTURE.md) records the continuing feature review,
architectural decisions and unresolved design questions. This README owns the
project's purpose and strategic constraints; the architecture document owns the
more detailed decisions as they are made.

## Open design work

The following remain deliberately unresolved:

- Concrete module boundaries, execution model, language and library choices.
- Source/build packaging, the custom-build workflow and release/update mechanics.
- Persistence, restart/recovery, backend discovery and duplicate-start handling.
- Background-app controls, tray integration, startup-at-login and shutdown rules.
- Network access and authentication boundaries for LAN/Tailscale use.
- Detailed implementation sequencing and expansion beyond Windows.
- Behavioural acceptance evidence and practical measures of reduced maintenance
  and publication complexity.

Resolve these against the direction above. They are future design work, not
permission to reintroduce Pi coupling or a general-purpose customisation framework.
