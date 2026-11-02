# Pie direction investigation — shared brief

You are one of several independent investigators. Do your own research and reasoning; do not try to guess what answer is wanted. Disagreement with the framing below is welcome if evidence supports it. Do NOT edit any files in `C:\dev\repos\pie`. Read-only investigation. Write only to your assigned output file.

## What Pie is

Pie (`C:\dev\repos\pie`, ~6 months old, ~1,450 commits, single maintainer working mostly through AI agents) is an open-source (AGPL-3.0-only), developer-focused coding-agent application: a VS Code sidebar extension plus a standalone/browser host sharing one Node backend and a Preact UI. It aims to support the common workflows a developer needs (multi-session agents, subagents, cross-session coordination, provider/model management, MCP, web search, browser/computer use, analytics/cost tracking, durable transcripts/recovery). The stated philosophy: where Pie lacks something, the developer can ask their Pie agent to add the feature or integration.

Key docs: `README.md`, `docs/architecture/ARCHITECTURE.md`, `docs/plans/PIE-PRODUCTIZATION.md`, `docs/contracts/STATE_CONTRACT.md`.

### Relationship to Pi
Pie is built on Pi (`@earendil-works/pi-coding-agent`, https://pi.dev, Mario Zechner's minimal, extensible terminal coding agent). Pie vendors a fork of Pi v0.80.6 in `harness/pi/packages/{ai,agent,tui,coding-agent}` (+ a Pie-added `orchestrator`). Since import, Pie changed ~98 files / +18k lines in the fork (largest: `agent-session.ts`, `session-manager.ts`, model-context policy, durable records). Pie uses Pi's SDK in worker processes (`harness/agent-processes/lib/sdk-integration/sdk.ts`, `workers/runtime-factory.ts`), Pi's provider layer (`pi-ai`), and Pi's extension/resource-loading mechanisms (Pie tools under `harness/tools`, discovery adapters under `extensions/`, plus third-party Pi packages `pi-mcp-adapter` and `pi-web-access`). Pi's terminal UI (`pi-tui`) is not Pie's UI.

### Size (non-test source lines, approx.)
- Pi fork: coding-agent 76k, ai 39k, tui 12k, agent 8.5k
- Pie: frontend 51k, agent-processes 47k, backend 46k, tools 27k, analytics 27k, scripts (build/install/verify) 22k, session-storage 14k, hosts 11k, model-providers 8.5k, plus misc. ~270k+ non-test lines; tests roughly equal again.
- Delivery: VSIX ~318 MB (bundled Node, Chromium/Playwright, Pi runtime artifact with manifest/fingerprints, immutable generations with leases and rollback).

## The decision at hand

Pie is moving from "maintainer's personal discovery project" to "product other developers install". The recent discussion (summarised) went:

1. A Windows VSIX can be built; Marketplace release still needs fresh-user onboarding, licensing/notice review, naming, clean-machine acceptance.
2. Customization model currently planned: packaged defaults + user/project layers for instructions, agents, skills, MCP config, provider config, and Pi-style executable extensions/tools. UI is not user-extensible; Pi extension UI hooks target Pi's terminal UI, not Pie's GUI.
3. Option raised: build a UI-extension system so user tools can bring their own GUI renderers and users can replace UX surfaces.
4. Counter-option raised: **source-first customization** — ship (or make trivially obtainable) the exact matching source with every build; users (via their Pie agent) modify Pie's source directly for anything beyond basic config. This could let Pie **remove** runtime customization machinery (extension loading, resource layering, plugin APIs, maybe Pi itself or large parts of it) and simplify the harness. Known costs: users need build tooling; local modifications must be reconciled with upstream updates; installed files must not be edited in place.
5. Also raised: starting a **fresh repository** that references the current one and rebuilds Pie step by step with the knowledge gained, rather than continuing to mature the current codebase in place.

Candidate directions (not exhaustive — propose others):
- **A. Runtime-extensible product**: keep Pi + layered customization; add UI extension points.
- **B. Source-first, simplified harness in place**: keep current repo; remove/replace runtime customization subsystems (and possibly Pi dependencies) incrementally; customization = edit source.
- **C. Fresh repo, source-first**: new repo, lean core, port proven pieces from current Pie deliberately.
- **D. Hybrid**: e.g. small stable config surface (instructions/skills/agents/MCP/providers as data) + source-first for code-level changes; keep or trim Pi selectively (e.g. keep `pi-ai` providers, drop extension/resource/TUI layers).

Constraints / facts to weigh: single maintainer + AI agents; long-term maintainability is the top concern; the wrong path could cost months; it's open source and dev-focused; users are developers who have a coding agent at hand.

## Addendum — maintainer answers and phase-1 facts (added after phase 1)

Maintainer answers:
- Updates for modified installs: up to the user. Most users will make minor adjustments and manage their local installation themselves; some will create their own repo from their local copy or fork Pie.
- Entry point: VS Code Marketplace (prebuilt) is the #1 entry point; git clone is supported but secondary; other mediums later.
- Fresh-repo driver: re-architect around packaging source and dropping supported runtime-customizability complexity, around now-settled patterns. Maintainer believes the codebase (~570k lines incl. tests; ~270k non-test Pie source + ~136k Pi fork) is too big for what Pie is.
- Scope: essentially all current features are core (refined UI, agent tooling, dynamic tool discovery, token/tool-result pruning, session controls, subagents, analytics...). If rebuilt, more investment would go into subagents, e.g. subagents surviving parent interruption and continuing when the parent session resumes.

Phase-1 facts (reports in this folder: report-harness-survey.md, report-malleable-precedents.md, report-complexity-audit.md):
- Pi fork base is v0.80.6 (2026-07-09). Upstream has shipped 32 releases since (1.0.0 on 2026-10-01 … 1.4.0 on 2026-10-21). The fork has never been resynced; changes to agent-session.ts/session-manager.ts are deep. Pie effectively owns its Pi fork now.
- Pie's own tools are registered through Pi's ExtensionAPI (extensions/* adapters, harness/tools/*); MCP and web search are third-party Pi packages (pi-mcp-adapter, pi-web-access).
- Pi customization machinery (extension loader ~4.4k, resource loader ~1.9k, package manager ~2.7k, settings manager ~1.2k, TUI/themes ~13k) ≈ 23k lines.
- The plan notes git history contains personal identities requiring a privacy decision before publishing.
