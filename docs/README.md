# Documentation

Guides describe implemented behavior. Decisions distinguish agreed requirements from proposals.
Evidence records describe only their dated test/research scope; old test totals are not the
current suite size. Git history retains deleted drafts and superseded comparisons.

## Current guides

- [Backend development](backend-development.md): SDK, Core contracts, builds and checks.
- [Local server](server-development.md): launch, data ownership, HTTP and event APIs.
- [Browser development](browser-development.md): UI toolchain, behavior and verification.
- [Provider management](provider-management.md): connections, credentials and browser settings.
- [Test scope](testing.md) and [code shape](code-shape-guidelines.md): local verification,
  size budget and review conventions.

## Decisions

- [Product direction](product-direction.md): purpose, constraints and their rationale.
- [Architecture](ARCHITECTURE.md): agreed boundaries, retained features and open choices.
- [Application structure](application-plan.md): current/planned owners and scoped review.
- [Sessions](session-design.md), [execution](execution-design.md) and
  [discovery](discovery-design.md): lifetime, model/tool calls and capability exposure.
- [Browser](BROWSER-DESIGN.md): shared UI, local access and remaining host/closure choices.
- [Transcript](TRANSCRIPT-DESIGN.md) and [stage-1 plan](TRANSCRIPT-HISTORY-PLAN.md):
  approved requirements and staged work. [Implementation evidence](transcript-history-implementation.md)
  identifies delivered stage-1 behavior. Stage 2 is active; these files retain their names
  and ownership while that work proceeds.
- [Source distribution](repo-foundations.md): matching source, custom-copy data and setup boundaries,
  with a dated round-trip trial; packaging and activation remain unfinished.

## Evidence

These records preserve measurements, source findings or constraints still used by decisions.
Experimental source is [archived outside the checkout](product-direction.md#experimental-source-archive).

- [Provider qualification](provider-qualification.md): offline and separately authorized live route/model checks.
- [Model interface](model-interface-research.md), [provider research](provider-research.md),
  [context lifetime](CONTEXT-LIFETIME-EVIDENCE.md) and [cache behavior](cache-research.md).
- [MCP library](mcp-library-research.md) and [SDK qualification](mcp-qualification.md).
- [Access boundaries](access-boundary-evidence.md), [backend lifecycle](backend-lifecycle.md)
  and [durable intent/reference lifetimes](DURABLE-BOUNDARIES-EVIDENCE.md).
- [Storage research](STORAGE-RESEARCH.md), [binding review](storage-binding-review.md),
  [experiment contract](storage-experiment.md) and [SQLite validation](sqlite-validation.md).
- [Parser qualification](TRANSCRIPT-PARSER-EVIDENCE.md).
- [Small-operation proposal](experiment-proposal.md), [Windows results](experiment-results.md)
  and [Go/.NET discovery](go-dotnet-discovery.md): historical comparisons, not open language choices.

New filenames use lowercase-kebab-case. Transcript-owned files and their directly linked
legacy targets retain existing spelling until link-only renames can be coordinated.
