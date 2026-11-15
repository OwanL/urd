# Documentation

Guides describe implemented behavior. Decisions distinguish agreed requirements from proposals.
Evidence records describe only their dated test/research scope; old test totals are not the
current suite size. Completed plans, closed audits and superseded research live in Git history,
not a separate archive. Their remaining constraints belong to the current documents below.

## Current guides

- [Backend development](backend-development.md): SDK, Core contracts, builds and checks.
- [Local server](server-development.md): launch, data ownership, HTTP and event APIs.
- [Browser development](browser-development.md): UI toolchain, behavior and verification.
- [Provider management](provider-management.md): connections, credentials and browser settings.
- [Usage accounting](usage-accounting.md): API-equivalent cost, durable records and live-estimate limits.
- [Test scope](testing.md) and [code shape](code-shape-guidelines.md): local verification,
  size budget and review conventions.

## Decisions

- [Product direction](product-direction.md): purpose, constraints and their rationale.
- [Architecture](ARCHITECTURE.md): agreed boundaries, storage choice and open decisions.
- [Application structure](application-plan.md): current/planned owners and scoped review.
- [Sessions](session-design.md), [execution](execution-design.md) and
  [discovery](discovery-design.md): lifetime, model/tool calls, adapter limits and capability exposure.
- [Agent tools](agent-tools-design.md): tool set, parallel/background calls, approved class shapes.
- [Compaction](compaction-design.md): implemented history compaction and its limits.
- [Browser](BROWSER-DESIGN.md): shared UI, local access and remaining host/closure choices.
- [Transcript](TRANSCRIPT-DESIGN.md): approved requirements, delivered foundation and remaining
  staged work. Stage 2 is active; historical checkpoints are not overall acceptance.
- [Source distribution](repo-foundations.md): matching source, custom-copy data and setup boundaries,
  with a dated round-trip trial; packaging and activation remain unfinished.

## Evidence

These records preserve qualification or constraints still used by decisions.
Experimental source is [archived outside the checkout](product-direction.md#experimental-source-archive).

- [Pruning replay results](pruning-replay-results.md): dated real-model selection,
  timing and recovery evidence; no pruning bundle qualified, and no startup winner
  or production loading policy is established.
- [Agent-tool qualification](agent-tools-qualification.md): scoped Windows process, adapter and
  live acceptance checkpoints; not universal tool/provider qualification.
- [Provider qualification](provider-qualification.md): offline and separately authorized live route/model checks.
- [MCP library](mcp-library-research.md) and [SDK qualification](mcp-qualification.md): selected library,
  protocol boundaries and remaining integration gates.
- [Parser qualification](TRANSCRIPT-PARSER-EVIDENCE.md) and
  [position lookup](TRANSCRIPT-POSITION-EVIDENCE.md): transcript feasibility and unclosed gates.

New filenames use lowercase-kebab-case. Transcript-owned files and their directly linked
legacy targets retain existing spelling until link-only renames can be coordinated.
