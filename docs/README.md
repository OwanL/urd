# Documentation

Keep a document here if it explains current behavior, records a decision that still governs
work, or helps resume unfinished work. Keep dated evidence only where an active decision or
qualification gate needs it. Fold surviving constraints into the guide that owns them; let
superseded research and plans live in Git history, not an archive directory.

## Current guides

- [Backend development](backend-development.md): SDK, Core contracts,
  [history compaction](backend-development.md#history-compaction), builds and checks.
- [Local server](server-development.md): launch, data ownership, HTTP/LAN access and event APIs.
- [Browser development](browser-development.md): UI toolchain, behavior and verification.
- [Provider management](provider-management.md): connections, credentials and browser settings.
- [Usage accounting](usage-accounting.md): API-equivalent cost, durable records and live-estimate limits.
- [Web access](web-access-plan.md): search and read tools, limits and remaining qualification.
- [Test scope](testing.md) and [code shape](code-shape-guidelines.md): local verification,
  size budget and review conventions.

## Decisions

- [Product direction](product-direction.md): purpose, constraints and their rationale.
- [Architecture](ARCHITECTURE.md): agreed boundaries, storage choice and open decisions.
- [Sessions](session-design.md), [execution](execution-design.md) and
  [discovery](discovery-design.md): lifetime, model/tool calls, adapter limits and capability exposure.
- [Prepass plan](pruning-plan.md): parallel pruning and title generation, protection/model
  settings, implemented milestones and their verification limits.
- [Agent communication](agent-communication-design.md): bounded delegation, request/reply,
  ownership and cycle handling; backend behavior and deferred UI work.
- [Agent tools](agent-tools-design.md): tool set, parallel/background calls, approved class shapes.
- [Browser](BROWSER-DESIGN.md): shared UI, local access and remaining host/closure choices.
- [Playwright automation](playwright-design.md): shared browser profile, sign-in handoff and first implementation slice.
- [Transcript](TRANSCRIPT-DESIGN.md): approved requirements, delivered foundation and remaining
  staged work. Stage 2 is active; historical checkpoints are not overall acceptance.

## Windows release

- [First Windows release plan](first-windows-release-plan.md): portable build and packaging,
  clean-machine and release gates.
- [Private Windows trial handoff](private-windows-trial-handoff.md): retained candidate, evidence
  and resume steps.

## Evidence

These records preserve qualification or constraints still used by decisions.
Experimental source is [archived outside the checkout](product-direction.md#experimental-source-archive).

- [Pruning classification](pruning-classification-results.md) and
  [full classification CSV](pruning-classification-summary.csv): asymmetric error costs,
  accuracy and latency across all replay blocks; conservative parallel selection is
  the low-latency candidate, not a proven low-risk winner over keep-all.
- [Agent-tool qualification](agent-tools-qualification.md): scoped Windows process, adapter and
  live acceptance checkpoints; not universal tool/provider qualification.
- [Provider qualification](provider-qualification.md): offline and separately authorized live route/model checks.
- [MCP library research](mcp-library-research.md) and [SDK qualification](mcp-qualification.md):
  library choice, bounded protocol evidence and remaining integration gates.
- [Parser qualification](TRANSCRIPT-PARSER-EVIDENCE.md) and
  [position lookup](TRANSCRIPT-POSITION-EVIDENCE.md): transcript feasibility and unclosed gates.

New filenames use lowercase-kebab-case. Transcript-owned files and their directly linked
legacy targets retain existing spelling until link-only renames can be coordinated.
