# Foundation research: runtime cost and local customisation

> **Subsequent decision (2026-11-03):** C#/.NET is selected for the backend.
> Language shortlists and recommendations below are historical, not open direction.
> See [the agreed decision](ARCHITECTURE.md#c-backend-and-class-design).

## Status and decision boundary

Research on 2026-11-03, before the C# backend decision; no language, runtime,
SDK, storage or UI toolkit was selected by this investigation. The then-provisional
TypeScript recommendation is now superseded. This screen combines delegated
primary-source investigations with follow-up checks;
it is not a benchmark, implementation-cost measurement or compatibility guarantee.
No toolchains were installed, applications built, provider accounts used or model
requests made for this investigation.

[README.md](README.md) and [ARCHITECTURE.md](ARCHITECTURE.md) own the requirements.
[PROVIDER-RESEARCH.md](PROVIDER-RESEARCH.md) owns the earlier transport/loop probes;
those Node-based checks do not qualify Bun, Go, Rust or .NET, or establish a runtime
performance winner. [STORAGE-RESEARCH.md](STORAGE-RESEARCH.md) remains undecided.

The subsequently expanded [candidate inventory](FOUNDATION-CANDIDATES.md) lists
additional languages, runtimes, frameworks and packaging approaches without
ranking them. The proposed research order below does not restrict that inventory.
A subsequent [first-pass ranking](FOUNDATION-RANKING.md) compares broader candidate
families and shared-interface approaches; coverage is not equal-depth for every
package, and no resource measurements or stack selection have been made.

## What a good fit must satisfy

- Keep Urd's own CPU and memory overhead low within reason, leaving resources for
  agents' compilers, shells, browsers and local models. Small first-party LOC does
  not by itself establish a small runtime footprint.
- Keep installation, library integration and long-term maintenance manageable.
  Preserve the useful capabilities already agreed, including AI catalog pruning,
  delegation, website automation and desktop control.
- Ship source that users and their agents can modify and build **on their own
  machines**. Evaluate that workflow separately from running a prebuilt release.
- Preserve the first-party harness boundary: no Pi dependency or another complete
  agent application's runtime as the foundation. Focused libraries and optional
  loop libraries remain eligible.
- Remain Windows-first, browser-first and compatible with the planned shared
  backend and additional clients. Neither a single executable nor a particular
  desktop shell is a requirement.

A source archive alone does not establish a usable customisation workflow. The
comparison must include prerequisites, dependency restoration, local build/test,
activation/restart and the resources consumed by those operations. Build/reload
is acceptable; seamless backend hot-swapping is not required. Whether compilers
or dependency caches ship with the source remains open; local building does not
by itself promise offline or bit-for-bit reproducible builds.

## What comparable applications actually do

These are architectural examples, **not eligible replacement runtimes or proof of
resource efficiency**. Source links pin the inspected implementations. A native
backend does not remove the cost of its desktop UI or external tools.

| Application | Observed execution boundary | Relevant lesson |
| --- | --- | --- |
| [Codex](https://github.com/openai/codex/tree/2816088b68069dd5ce1f71a653cd956eb367c28e) | Rust agent core; npm JavaScript launches the native binary. Optional app-server exists. This is CLI source, not the separate desktop app's implementation. | Installing a native Windows release is not the same as building it: the inspected [build guide](https://github.com/openai/codex/blob/2816088b68069dd5ce1f71a653cd956eb367c28e/docs/install.md) documents Windows development through WSL2. |
| [Goose](https://github.com/aaif-goose/goose/tree/cfb67d72f6129ba3c9f68d4c225d8dfcaba18a87) | Rust agent/backend; Electron/React/TypeScript desktop starts the Rust server. | Backend and desktop resource costs are distinct; changing both spans Rust and JS toolchains. |
| [OpenCode](https://github.com/anomalyco/opencode/tree/1bf08ae46a91f7793c756e4ee521e37b6b554064) | TypeScript/Bun core and CLI/service; the inspected [desktop](https://github.com/anomalyco/opencode/blob/1bf08ae46a91f7793c756e4ee521e37b6b554064/packages/desktop/README.md) is Electron and bundles/connects to a separate CLI service. | TypeScript can support a shared service architecture; this does not establish Bun's cost for Urd. Do not conflate the desktop shell with the agent runtime. |
| [Crush](https://github.com/charmbracelet/crush/tree/3b1f8604f0027cf5fa47f29a27655ef1aeed8d8f) | Go app uses the focused Fantasy agent/model library; default TUI and agent are in-process, with optional server mode. | A Go backend need not hand-write all provider normalisation or loop mechanics. Urd does not need its TUI. |
| [Pi](https://github.com/earendil-works/pi/tree/00967748dc5a71e939214f962906d3c2d06b7c2a) | TypeScript agent loop and provider packages; Node-based coding-agent runtime, with optional RPC boundary. | The loop/client split is a useful reference, not permission to depend on Pi. |

The bounded source review found Goose's stated Rust rationale of performance and
portability, but no controlled cross-language evidence applicable to Urd. Other
projects' language choices, repository size and download counts are not cost
measurements.

## Plausible candidates at first glance

The backend is intended to stay alive across UI connections. Its startup cost is
therefore different from launching a new agent process for every task. Measure
per-context growth, materialised histories, buffering/copies and auxiliary process
lifetimes alongside the runtime floor; a language change does not by itself fix
an expensive process or data-ownership design.

For comparison, a JS/TS web client is a useful common assumption, **not a selected
UI implementation**. Compiled backends would then add a backend toolchain and a
cross-language client/server contract; both costs must be counted, not exaggerated
into a claim that such architectures are inherently large.

A C# backend does **not inherently require a JS/TS browser-UI build toolchain**:
[Blazor tooling](https://learn.microsoft.com/en-us/aspnet/core/blazor/tooling?view=aspnetcore-10.0)
uses the .NET SDK. Its [render modes](https://learn.microsoft.com/en-us/aspnet/core/blazor/components/render-modes?view=aspnetcore-10.0)
trade server-side circuit resources and connection-dependent interaction against
browser-side .NET/WebAssembly download and execution. This could change the
customisation comparison, but needs its own responsiveness/resource evaluation;
it does not select Blazor. A VS Code host bridge is a separate boundary.

| Candidate | Why it deserves consideration | Main qualification risk |
| --- | --- | --- |
| TypeScript + Node | Provider/auth/MCP ecosystem; potential language/type sharing with clients; ordinary bundled-runtime distribution avoids requiring a global Node installation. | JS heap/GC and synchronous work can affect the shared backend. Native addons, packaging and required Windows tools need verification. Existing SDK mock checks are not resource measurements. |
| TypeScript + Bun | Same broad JS ecosystem, integrated build/package tooling and standalone executables; a particularly relevant local-customisation possibility described below. | Node compatibility is not universal. Exact provider, subprocess, native-module and browser paths must work on the pinned Windows runtime. Do not infer low RAM from startup/HTTP marketing benchmarks. |
| Go | Native executable distribution and explicit concurrent execution; official provider/MCP SDKs and Fantasy offer library reuse. | Native Windows desktop-control integration remains unqualified. A JS UI adds a toolchain; browser bindings may still launch Node. GC, allocations and retained payloads still matter. |
| C#/.NET | Official provider SDKs, Microsoft.Extensions.AI, official MCP and Playwright bindings, and Windows APIs are relevant to the retained features. | Self-contained JIT and Native AOT have different resource, compatibility and local-build costs. AOT cannot simply be combined with every Windows/UI library. |
| Rust | Native execution, no tracing garbage collector and explicit ownership; focused model/MCP/Windows libraries exist. | Default Windows source builds need MSVC tooling; browser bindings/provider coverage and library maintenance need qualification. Neither lower total RAM nor acceptable edit/build cost has been demonstrated. |

### Additional candidates not ruled out

The bounded completeness check also found plausible **Python** and **Deno** paths.
[Python/PydanticAI](https://github.com/pydantic/pydantic-ai) offers model/agent
library facilities alongside an official MCP Python SDK, while
[uv](https://github.com/astral-sh/uv) manages Python and project environments.
Interpreter/environment distribution, native wheels and the chosen adoption scope
still need evaluation. Focused loop facilities remain eligible; this is not
approval to adopt a complete coding-agent product.

[Deno](https://docs.deno.com/runtime/reference/cli/compile/) combines TypeScript
tooling and standalone compilation, with documented
[Node/npm compatibility caveats](https://docs.deno.com/runtime/fundamentals/node/).
It could be another runtime for a TS implementation, not necessarily an extra
runtime alongside Node. These were lighter availability checks, not equal-depth
qualifications or resource rankings. Neither candidate is disqualified; evidence
of better required-library fit or source-build experience can change prioritisation.

### Focused libraries, not complete agent applications

- **TS:** Vercel AI SDK or vendor clients; `openid-client`; official MCP TS SDK.
  Existing research leaves model-only versus managed-loop ownership open.
- **Go:** official OpenAI and Anthropic clients, official MCP Go SDK, and
  [Fantasy](https://github.com/charmbracelet/fantasy/tree/bd958397da5024cb6273adf2566ff97aaad7395b).
  Fantasy exposes normalised model/stream types and an optional agent/tool loop;
  model-only calls remain possible. Reviewed main and observed release v0.45.5
  are distinct references, not a selected Urd dependency. Cancellation semantics
  and lifecycle hooks still need behavioral qualification.
- **C#:** official [OpenAI](https://www.nuget.org/packages/OpenAI) and
  [Anthropic](https://www.nuget.org/packages/Anthropic) clients;
  [Microsoft.Extensions.AI](https://learn.microsoft.com/en-us/dotnet/ai/microsoft-extensions-ai)
  provides common chat/stream/usage APIs and tool-invocation middleware; official
  [MCP C# SDK](https://github.com/modelcontextprotocol/csharp-sdk). Broader agent
  frameworks are optional candidates, not automatically necessary or forbidden.
- **Rust:** [genai](https://github.com/jeremychone/rust-genai) supplies a
  multi-provider interface; [Rig](https://github.com/0xPlaygrounds/rig) offers
  agent/loop facilities; official [MCP Rust SDK](https://github.com/modelcontextprotocol/rust-sdk)
  and OAuth/Windows integration crates exist. Availability does not establish the
  required combinations' coverage or maintenance cost.

This is an availability screen. A provider adapter, OAuth library or Copilot-named
module does not establish subscription entitlement, approved endpoints or reliable
refresh. Claude remains API-only; remaining routes need their own qualification.

## Packaging and user-machine builds: consequential findings

### 1. Bun's ordinary executable can also expose its build CLI

The current [single-file documentation](https://bun.com/docs/compiler/executables)
says `bun build --compile` embeds the Bun runtime and supports Windows targets.
For the ordinary full executable, `BUN_BE_BUN=1` exposes the Bun CLI: it can install
packages, bundle dependencies and run files without a separate Bun download.
That is a concrete candidate for source-bundled local customisation, not just
convenient release installation. Dependency restoration, assets and the complete
Windows build/test path still need validation.

Do not confuse this with [experimental AOT](https://bun.com/docs/compiler/aot):
`bun compile` / `--aot` produces machine code, changes compatibility, has no JIT,
and does **not** include that CLI escape hatch. Experimental trimming can also
remove development facilities. The docs describe longer builds without an
incremental compilation cache and possible slow paths for untyped/generated code.
Those are material costs for user customisation. These are live-documentation
claims, not verified behavior of a chosen stable Bun release or Urd dependencies.

### 2. Node does not require single-executable packaging

Shipping a pinned Node runtime alongside built JS and source is a separate option
from [Node SEA](https://nodejs.org/docs/v26.13.0/api/single-executable-applications.html).
The reviewed version documents `--build-sea` and warns its creation process is
subject to change. A source-build workflow needs deliberately provisioned package
management/build dependencies; merely shipping `node.exe` does not supply them.

If a native addon must build from source, [node-gyp](https://github.com/nodejs/node-gyp)
adds Python and Visual C++ prerequisites on Windows. Compatible prebuilt addons
can avoid that path; native dependencies do not always force a compiler install.

### 3. Native release convenience can move cost onto customising users

- [Go](https://go.dev/doc/install) can avoid a separately installed language
  runtime. Pure-Go builds can avoid a C compiler; dependencies using
  [cgo](https://pkg.go.dev/cmd/cgo) change that. Do not promise a dependency-free
  executable without checking the actual graph and external tool binaries.
- [Rust's Windows installation](https://www.rust-lang.org/tools/install) adds MSVC
  build tools for the default toolchain. Native dependencies/linking still affect
  release packaging. Actual first and incremental build costs are unmeasured.
- [.NET single-file](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview)
  is orthogonal to framework-dependent versus self-contained deployment. A
  self-contained JIT release bundles .NET rather than requiring a separate runtime
  install. [Native AOT](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/)
  additionally requires Visual Studio C++ tooling on Windows; it retains runtime
  support and GC. The documented absence of built-in COM and restrictions on
  dynamic code/reflection mean Windows UI Automation/tray and chosen libraries
  cannot simply be assumed AOT-compatible. JIT development does not remove the
  need to verify the actual locally customised release format.

A separate-assets layout could let UI changes avoid recompiling a native backend;
that is a packaging proposal, not a new override framework or selected layout.
No source edit/build timings, compiler download sizes or binary-size rankings were
measured here.

### 4. A compiled backend does not necessarily eliminate Node

The inspected [Playwright .NET driver](https://github.com/microsoft/playwright-dotnet/blob/main/src/Playwright/Helpers/Driver.cs)
resolves a Node executable and JavaScript driver. The inspected
[Go binding](https://github.com/playwright-community/playwright-go/blob/main/run.go)
also launches Node and obtains driver/runtime artifacts unless externally supplied.
These are moving-source observations accessed on the research date, not pinned
Urd dependencies. Browser binaries and their process trees are additional costs.
Count them when comparing a candidate's complete retained capability set; do not
call a native backend the whole resource footprint. Other integration approaches
remain possible but must preserve the agreed browser capability.

## Recommendation: shortlist and qualification order, not selection

**TypeScript remains plausible, but its supposed integration advantage is not a
sufficient basis for commitment. Go is a serious alternative, not a bare-HTTP
rewrite.** Bun deserves explicit investigation because its build-CLI packaging
could fit the source-customisation goal unusually well. Node remains the JS
compatibility baseline. C#/.NET is a credible cross-platform backend candidate;
its Windows integration options do not establish portable desktop-host or tool
coverage. Rust is credible when measured resource/control benefits justify its
local-build and integration costs. No RAM winner has been established.

**Scope correction:** The intended OS rollout is Windows → macOS → Linux, not
Windows with unspecified later platforms. This report's initial qualification
order was Windows-centric. Before selection, assess the full stack's macOS/Linux
integrations, packaging and local source builds; see the
[ranking portability correction](FOUNDATION-RANKING.md#portability-correction-windows--macos--linux).

Proposed next sequence:

1. Qualify a minimal required-library/local-build path before building full
   prototypes: provider streaming/tool calls, OAuth plumbing, MCP subprocesses,
   Windows desktop/browser boundary and packaged-source rebuild. Keep real
   subscription entitlement checks separate from offline model mocks.
2. Prioritise a common TS fixture on Node/Bun and a Go/Fantasy fixture. Keep C#
   and Rust in the comparison: a blocking integration gap or an unacceptable
   measured footprint can change which compiled candidate reaches the benchmark.
   This is research sequencing, not rejection of their architectures.
3. Measure finalists with equivalent behavior and payloads. Select only after
   weighing runtime cost, local customisation, owned integration code and library
   maintenance together. Do not build a generic five-language harness framework.

### Bounded measurement contract for finalists

- Separate backend floor, idle-context growth and active work. Use 1/50/210
  contexts as exploratory cases, with active stream count and payload sizes
  disclosed. Do not allocate one OS process per context merely to run a test.
- Use deterministic mock streams/tools and synthetic histories. Exercise small
  and long histories, compaction-derived context, bounded visible-history reads,
  streaming, tool admission and release of completed contexts. Match observable
  behavior before comparing implementation size or resource use.
- Record startup-to-ready and click-to-**visible content**, not just response
  acknowledgement. Measure under declared mock load; distinguish cold process,
  cold session hydration and warm cache from genuinely cold filesystem caches.
- Record CPU time, allocation/GC or scheduler stalls, private committed memory,
  per-process working set, peaks and post-quiescence levels. Do not sum shared
  working-set pages as unique physical memory or force GC as normal lifecycle.
  [Windows process-memory semantics](https://learn.microsoft.com/en-us/windows/win32/api/psapi/ns-psapi-process_memory_counters_ex)
  and [runtime counters](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/available-counters)
  inform instrumentation; language-specific counters are explanatory, not scores.
- Report browser/tool/driver processes separately, and then the full declared
  scenario. Separate local-model and compilation workloads from the harness
  baseline rather than hiding or charging them to the wrong component.
- Separately exercise a clean user-machine source build and representative
  guidance/UI/tool/backend edits. Record prerequisite installs/downloads, first
  and incremental build/test/restart steps, time, disk, peak CPU and memory.
  Count any extra client/native-helper toolchains and production-format failures.
- Use comparable production builds, pinned versions, the same machine/data/load,
  repeated runs and alternating order. Preserve sample counts and raw results;
  report uncertainty, not a winner within measurement noise. No numerical resource
  or latency acceptance thresholds have been agreed; comparative measurements
  remain useful without inventing pass/fail budgets.

## Evidence trail and limits

Delegated reports and follow-up notes are local research artifacts under
`%TEMP%\urd-foundations-9hGfO2\`; this document preserves the reviewed findings
and public references rather than relying on those temporary files remaining.
Library/version availability and moving documentation must be rechecked at
qualification time. No installed-size, RAM, CPU, throughput, build-time or
maintained-LOC comparison has yet been performed. This screen cannot guarantee
that a foundation will never need to change; it identifies the evidence needed
to avoid choosing from language stereotypes or release-install convenience alone.
