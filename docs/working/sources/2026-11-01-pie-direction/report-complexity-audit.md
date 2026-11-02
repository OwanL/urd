# Pie complexity and Pi-dependency audit — facts for the direction decision

Scope: read-only inspection of `C:\dev\repos\pie`. Line counts are non-test source lines (`.ts/.tsx/.js/.mjs`) after excluding `node_modules`, `.git`, `.cache`, `dist`, `out`, `out-dev`, `build`, coverage, and `*.test.ts`/`*.spec.ts` paths.

## 1. Pi packages Pie actually uses at runtime

| Pi package | Source lines in fork | Pie files that import it (static) | What Pie uses it for |
|---|---|---|---|
| `@earendil-works/pi-ai` | ~39,000 | 11 | Provider/model types, streaming (`stream` from `api/openai-completions`), `StringEnum`, `completeSimple`, OAuth surface. |
| `@earendil-works/pi-agent-core` | ~8,500 | 2 (only types in tests) | Not the active loop in production; the real agent loop is inside `pi-coding-agent`. |
| `@earendil-works/pi-coding-agent` | ~77,000 | 52 + dynamic load in `harness/agent-processes/lib/sdk-integration/sdk.ts` | Session manager, `AgentSession` runtime, compaction, auth storage, model registry, built-in tools, extension/tool API. |
| `@earendil-works/pi-tui` | ~12,000 | 1 (`harness/tool-and-skill-selection/lifecycle/render.ts`) | Terminal UI primitives/types; Pie’s GUI is Preact, so this is basically dead weight. |

### (a) Provider / LLM layer (`pi-ai`)
- Runtime imports: `harness/tool-and-skill-selection/prepass/prepass.ts`, `harness/model-providers/management/test/*` use `stream` from `@earendil-works/pi-ai/api/openai-completions`; `harness/tools/playwright/schema.ts`, `harness/tools/computer-use/schema.ts`, and web-access guard tests use `StringEnum`/types from `pi-ai`.
- `harness/agent-processes/lib/sdk-integration/sdk.ts` dynamically resolves `pi-ai/compat` from the pinned SDK artifact to get `getModels(provider)`.
- `pi-ai` owns all provider SDKs (OpenAI, Anthropic, Google, Bedrock, Mistral), model discovery, retries, and OAuth. **Verified by `rg "@earendil-works/pi-ai"`.**

### (b) Agent loop (`pi-agent-core`)
- Static imports from `pi-agent-core` in Pie code are almost exclusively types (`AgentEvent`, `AgentMessage`, `AgentToolResult`).
- The actual loop is `pi-coding-agent/src/core/agent-session.ts` (4,668 lines) and the underlying `Agent` abstraction it consumes. `pi-agent-core` is therefore a transitive dependency, not a direct runtime seam.

### (c) Coding-agent session / session-manager / compaction / tools (`pi-coding-agent`)
- `harness/agent-processes/lib/sdk-integration/sdk.ts` dynamically loads a *verified* Pi artifact: `dist/index.js`, `core/auth-storage.js`, `core/model-registry.js`, `core/session-manager.js`, `core/model-resolver.js`.
- Pie directly consumes: `SessionManager`, `AgentSession`, `AuthStorage`, `ModelRegistry`, `prepareCompaction`/`compact`, `createAgentSessionServices`/`createAgentSessionFromServices`/`createAgentSessionRuntime`.
- Core Pi files Pie leans on (~16,800 lines):
  - `harness/pi/packages/coding-agent/src/core/agent-session.ts` — 4,668
  - `harness/pi/packages/coding-agent/src/core/session-manager.ts` — 2,900
  - `harness/pi/packages/coding-agent/src/core/model-registry.ts` — 1,032
  - `harness/pi/packages/coding-agent/src/core/model-resolver.ts` — 705
  - `harness/pi/packages/coding-agent/src/core/auth-storage.ts` — 669
  - `harness/pi/packages/coding-agent/src/core/model-context-period.ts` — 616
  - `harness/pi/packages/coding-agent/src/core/agent-session-runtime.ts` — 856
  - `harness/pi/packages/coding-agent/src/core/tools/*` — ~3,000
  - `harness/pi/packages/coding-agent/src/core/compaction/*` — ~1,400

### (d) Customization machinery
Files and sizes inside the fork:

| Machinery | File(s) | Lines |
|---|---|---|
| Extension API + loader + runner | `harness/pi/packages/coding-agent/src/core/extensions/*` | ~4,400 |
| Resource loader (skills, prompts, themes, AGENTS.md) | `resource-loader.ts` + `skills.ts` + `prompt-templates.ts` | ~1,900 |
| Package manager (npm/git/local install) | `package-manager.ts` | 2,746 |
| Settings manager (config layering) | `settings-manager.ts` | 1,235 |
| TUI / themes | `pi-tui` package + `modes/interactive/theme/theme.ts` | ~13,300 |
| **Total customization machinery** | | **~23,500** |

**Pie features implemented through Pi extensions / managed packages:**

| Feature | Where it lives | Extension/managed entry |
|---|---|---|
| Subagent tool | `harness/tools/subagent/*` | `extensions/subagent/index.ts` |
| Computer-use tool | `harness/tools/computer-use/*` | `extensions/computer-use/index.ts` |
| Playwright tool | `harness/tools/playwright/*` | `extensions/playwright/index.ts` |
| Bash override / warm-bash | `harness/tools/warm-bash/*` | `extensions/warm-bash/index.ts` |
| Ask-user tool | `harness/tools/ask-user/*` | `extensions/ask-user/index.ts` |
| Session control / collaboration | `harness/tools/session-control/*` | backend assembly in `harness/agent-processes/coordinator/backend-tools.ts` |
| Session changes | `harness/tools/session-changes/*` | `extensions/session-changes/index.ts` |
| Deferred triggers / wakeup timer | `harness/tools/deferred-triggers/*` | `extensions/deferred-triggers/index.ts` |
| Skill pruning / `request_capability`, `invoke_tool` | `harness/tools/request-capability/*`, `harness/tools/invoke-tool/*` | `extensions/skill-pruner/index.ts` |
| Safeguard (bash/write guard) | `harness/tools/execution-safety/*` | `extensions/safeguard/index.ts` |
| Tool-result pruner | `harness/tools/result-processing/*` | `extensions/tool-result-pruner/index.ts` |
| Web search / fetch (pi-web-access) | `lib/managed-packages/integrations/package.json` pins `pi-web-access@0.27.0`; guarded by `harness/tools/package-integrations/web-access/index.ts` | managed package |
| MCP adapter (pi-mcp-adapter) | `lib/managed-packages/integrations/package.json` pins `pi-mcp-adapter@2.20.1` | managed package |
| Copilot model discovery | `extensions/copilot-model-discovery/index.ts` | extension |
| CWD skills | `extensions/cwd-skills/index.ts` | extension |

Catalog source: `harness/tools/catalog/index.ts` enumerates all Pie tools and notes which are extension-registered vs backend-registered. About **29,000 lines of Pie code** are already structured as Pi extensions or managed-package glue (`harness/tools` ~26,900 + `extensions/` ~760 + `lib/managed-packages` ~1,820).

## 2. Pi fork: what Pie changed and whether upstream merges are still practical

Base: `44caef3a1` “Import pristine Pi v0.80.6 at harness/pi”. Current HEAD: `cc82abb0f`.
Diff summary: `98 files, +18,437 / -737` in `harness/pi` — effectively all additive.

Top changed source files:

| File | +/- | What changed (from commit messages / file content) |
|---|---|---|
| `packages/coding-agent/src/core/agent-session.ts` | +1,563 / -142 | Model-context periods, frozen tool targets, tool-result persistence, compaction hooks, interrupted continuation, retry semantics. |
| `packages/coding-agent/src/core/session-manager.ts` | +1,372 / -96 | Atomic durable records, Pie staging/ownership, session replacement transactions, snoozing. |
| `packages/coding-agent/src/core/agent-session-runtime.ts` | +439 / -17 | Runtime construction seams for Pie worker integration. |
| `packages/coding-agent/src/core/model-context-period.ts` | +615 / 0 | New Pie subsystem for immutable model-context windows. |
| `packages/coding-agent/src/core/auth-storage.ts` | +162 / -33 | Async/cancellable auth storage. |
| `packages/coding-agent/src/core/extensions/compiled-loader.ts` | +266 / 0 | Compiled extension inventory for worker isolation. |
| `packages/coding-agent/src/core/extensions/compiled-source.ts` | +227 / 0 | Compiled source cache for isolated runtimes. |
| `packages/coding-agent/src/core/package-manager.ts` | +129 / -29 | Default extension fallbacks for application defaults. |
| `packages/coding-agent/src/core/resource-loader.ts` | +82 / -9 | Default-extension and skill-priority overrides. |
| `packages/coding-agent/src/core/tool-model-exchange-capture.ts` | +190 / 0 | Capture transcript tool/model exchange. |
| `packages/agent/src/agent-loop.ts` | +157 / -32 | Resolved trusted tool execution targets. |
| `packages/ai/src/api/openai-completions.ts` | +34 / -? | User-image and retry handling. |

Most of the +18k is tests and the new model-context/ownership subsystems. **Verdict:** the fork is no longer a shallow set of seams. Pie has rewritten core session/agent behavior (concurrency, durability, leases, model-context policy). Reconciling with upstream would be a manual merge of deeply incompatible state-machine changes. For practical purposes, upstream merges are impractical unless Pie stops changing `agent-session.ts` and `session-manager.ts`.

## 3. What would need replacing if Pi machinery (or Pi itself) were dropped

### Scenario A: drop Pi customization machinery, keep `pi-ai` + core agent loop/session runtime
Pi code removed: ~23,500 lines (extensions, resource/package manager, settings layering, TUI/themes).

Pie must replace:
- **Tool registration host** for ~29,000 lines of extension-shaped code. Today these register via `pi.registerTool()` / `pi.on()`. They would need a first-class Pie tool host, or direct calls into `pi-coding-agent`’s lower-level API if that is retained.
- **Skill/prompt/AGENTS.md discovery** — currently `resource-loader.ts`. A few hundred lines of explicit discovery would suffice for source-first.
- **Default model/instruction config** — currently `settings-manager.ts`. Pie already has `application/backend/settings` and `session-storage/settings`; the overlap can be collapsed.
- **Managed package loading** — `pi-mcp-adapter` and `pi-web-access` would have to be vendored or rewritten as ordinary source tools, killing the web-access source-patching guard (`harness/tools/package-integrations/web-access/index.ts`, ~1,400 lines of patch logic).
- **No runtime extension installs** — users edit source instead of `pi install`.

Estimated replacement glue: **3,000–5,000 lines** if the core agent/session runtime stays.

### Scenario B: drop Pi entirely
Pie would need to own:
- Provider layer + streaming normalization + OAuth (~39,000 lines of `pi-ai`).
- Agent loop abstraction (~8,500 lines of `pi-agent-core`, but the real loop is inside coding-agent).
- Session manager, agent session runtime, compaction, auth storage, model registry, built-in tools, tool execution semantics (~16,800+ directly referenced lines, plus many internals in coding-agent).
- The durable session format that `harness/session-storage/transcripts` already depends on.
- Extension/event API that Pie tools rely on.

Rough total core logic to rewrite: **~60,000–70,000 lines**. For a single maintainer using AI agents, this is a multi-month rewrite and a high risk of subtle regressions in concurrency, leases, and compaction.

## 4. Where Pie’s own ~285,000 source lines go

Pie source (excluding the Pi fork) by subsystem:

| Subsystem | Source lines | Test lines | Essential vs accidental |
|---|---|---|---|
| `application/frontend` | 55,529 | 59,202 | **Essential** — Preact UI is the product. |
| `application/backend` | 46,383 | 54,885 | **Essential** — host, protocol, composition, provider management, transcript delivery. |
| `harness/agent-processes` | 47,495 | 52,687 | **Essential** — coordinator, worker runtime, RPC, SDK integration; this is Pie’s multi-session architecture. |
| `harness/tools` | 26,881 | 39,546 | **Essential features**, but partly **accidental shape** because they are forced into Pi-extension form. |
| `analytics` | 26,711 | 24,615 | **Essential product differentiator** (cost/usage tracking). |
| `scripts` | 22,711 | 30,688 | **Mixed** — build/install/verification are necessary; delivery scripts (`scripts/build/pi-runtime.mjs` 45k, `playwright-runtime-payload.mjs` 68k, `node-runtime-payload.mjs` 24k, `publication.mjs` 20k, `pi-isolated-workspace.mjs` 19k) are **accidental complexity** from bundling Node/Chromium/Playwright into a 318 MB VSIX. |
| `harness/session-storage` | 14,044 | 11,291 | **Essential** but overlaps with Pi `session-manager.ts`; some duplication could be removed if session format were owned. |
| `application/hosts` | 13,150 | 12,916 | **Essential** — VS Code/standalone/browser hosts. |
| `harness/model-providers` | 8,658 | 10,523 | **Essential** provider management overlay, though it overlaps with `pi-ai`. |
| `lib` | 9,368 | 3,573 | **Essential glue** — structured logging, temp files, process lifecycle; `lib/managed-packages` (~1,820) is accidental if Pi packages are dropped. |
| `harness/tool-and-skill-selection` | 4,445 | 9,565 | **Bridge complexity** — much of it exists to adapt Pie behavior to Pi’s extension/resource model. |
| `harness/agent-instructions` | 1,737 | 2,651 | **Essential** — prompt assembly and resource roots. |
| `extensions/` | 762 | 0 | **Accidental** — shim adapters only needed because of Pi extension discovery. |
| `docs/` | 4,544 | — | **Process overhead**; `docs/contracts/STATE_CONTRACT.md` is 149 KB, `docs/plans/PIE-PRODUCTIZATION.md` 70 KB, `docs/plans/SESSION-COORDINATION-V2.md` 73 KB. Useful, but a sign that the system is being documented faster than it is being simplified. |

Other notable accidental-complexity clusters:
- **Delivery / immutable generations / leases:** `scripts/build/*`, `application/hosts/lib/renderer-delivery/*`, `harness/agent-processes/workers/worker-provider-network-lease.ts`, `harness/agent-processes/coordinator/server-types.ts` / `server.ts` operation-ledger logic.
- **Tests:** ~422,000 total test lines (including Pi fork), roughly 1.5× source. Many tests are tightly coupled to Pi internals; removing Pi would mean losing most Pi-specific tests.

## 5. Proven / worth porting vs. better left behind

**Port to a fresh repo:**
- `application/*` frontend/backend/protocol/hosts (~121,000 src lines) — the actual product surface.
- `harness/agent-processes` coordinator/worker/RPC/SDK integration (~47,500 lines) — the multi-session isolation model works.
- `harness/session-storage` ownership/lifecycle/transcripts (~14,000 lines) — durable sessions are a core value.
- `harness/model-providers` provider management overlay (~8,700 lines).
- `analytics/*` cost/usage/tracking (~26,700 lines).
- Specific tool implementations in `harness/tools/*` (subagent, computer-use, playwright, session-control) — keep the implementations, **re-plumb registration**.
- `lib/structured-logging`, `lib/process-lifecycle`, `lib/temporary-files` (~3,500 lines of shared glue).

**Leave behind:**
- The Pi fork wholesale, **unless** the project keeps `pi-ai` + core agent/session runtime as a vendored library. In a fresh repo, port the *concepts* (leases, durable records, model-context policy), not the files.
- Pi extension loader, resource loader, package manager, settings manager, TUI — roughly **23,500 fork lines**.
- The `extensions/*` shim adapters and `lib/managed-packages` patching guard if web/MCP integrations are owned directly.
- The VSIX delivery scripts that bundle Node/Chromium/Playwright if the project moves away from immutable binary distribution — tens of thousands of lines.
- The bulk of the Pi-specific test suite if Pi is removed.

## Bottom line

Pie is deeply dependent on `pi-coding-agent`’s session/runtime/agent loop and on `pi-ai`’s provider layer, but **not** on Pi’s extension/package/TUI machinery. The Pi fork has already diverged into a co-evolved dependency: upstream merges are impractical. The lowest-risk source-first path is **Option D** — keep `pi-ai` and the core `pi-coding-agent` runtime, delete the extension/resource/package/TUI layers, and wire Pie’s existing tools directly to the runtime. Dropping Pi entirely would require owning ~60k+ lines of battle-hardened session/compaction/provider logic and is not proportionate for the current team size.
