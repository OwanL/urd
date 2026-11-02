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

**Pie's core systems, guidance and settled workflows are the product baseline.**
Do not treat this project as an exercise in choosing an unrelated minimal agent
product or re-deciding whether Pie's core capabilities belong.

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
contracts and guidance before rebuilding each area. Preserve intent and useful
behaviour, not accidental file layouts, process boundaries or abstractions.
Obsolete systems and abandoned approaches are not part of the inheritance.

Software development is the initial focus, not a requirement to make all agent
work inherently about code. There is no separate general-purpose product to
build alongside the development-focused one.

## Decisions and rationale

### Own a first-party harness; remove Pi

**Pi will not be a dependency or runtime coupling.** Urd's harness will be
mostly bespoke, lightweight and aligned with what Pie offers. Core capabilities
belong in its design rather than being bolted onto another application's
extension seams.

Prefer purpose-built systems over a general framework for arbitrary harnesses.
Less generalisation and less customisation infrastructure are intentional;
deep customisability comes from access to the source instead.

Ideas and useful logic may be borrowed from other harnesses. First-party
ownership does not require inventing every algorithm or rejecting focused
libraries. It means Urd owns its architecture and behaviour. Specific language,
library and framework choices remain open.

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
ship initially. The first surface and subsequent delivery order remain open.
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
  systems and guidance are the starting point. Questions should resolve real
  ambiguity or a proposed departure, not reopen established product features.
- **Distinguish product continuity from implementation inheritance.** A fresh
  implementation is required; retaining obsolete architecture is not. Conversely,
  removing a core capability is a product decision, not an incidental MVP cut.
- **Keep systems first-class and purpose-built.** Evaluate abstractions against
  Urd's actual requirements, not hypothetical consumers or arbitrary extension
  scenarios. Source access is the escape hatch for deep customisation.
- **Stage the work without silently shrinking the destination.** Delivery
  milestones may be incremental; they do not redefine the agreed product.
- **Demonstrate improvement rather than assuming it.** Preserve useful behaviour
  while making the code easier to understand, change, build and publish. A new
  repository or a lower line count alone is not evidence of success.
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

## Open design work

The following remain deliberately unresolved:

- Concrete module boundaries, execution model, language and library choices.
- Source/build packaging, the custom-build workflow and release/update mechanics.
- Persistence, restart/recovery, backend discovery and duplicate-start handling.
- Background-app controls, tray integration, startup-at-login and shutdown rules.
- Network access and authentication boundaries for LAN/Tailscale use.
- Implementation sequencing, the first UI surface and expansion beyond Windows.
- Behavioural acceptance evidence and practical measures of reduced maintenance
  and publication complexity.

Resolve these against the direction above. They are future design work, not
permission to reintroduce Pi coupling or a general-purpose customisation framework.
