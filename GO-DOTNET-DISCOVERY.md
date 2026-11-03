# Go and C#/.NET: focused discovery

> **Subsequent decision (2026-11-03):** The owner selected C#/.NET for the backend,
> valuing readability with deliberate class design. The comparison and finalist
> recommendation below are historical; qualification gaps remain relevant.
> See [the agreed decision](ARCHITECTURE.md#c-backend-and-class-design).

Research and Windows probes: **2026-11-03**. This advances the comparison in
[FOUNDATION-RANKING.md](FOUNDATION-RANKING.md); this investigation selected no
stack, storage, UI or loop ownership. The owner requested research and small experiments.
[README.md](README.md) and [ARCHITECTURE.md](ARCHITECTURE.md) remain authoritative.

## Bottom line

Both candidates have credible focused libraries; neither requires writing all
provider normalization, MCP or the model/tool loop from scratch. Both passed the
bounded streaming/control experiments below. There is no measured application
resource, maintained-LOC or productivity winner.

- **Go's strongest case:** a small standard-library server, straightforward
  backend builds, and Fantasy's directly exposed step-control API. Wails offers
  a same-language desktop-host candidate, but adds native build prerequisites.
- **C#'s strongest case:** ASP.NET Core, official provider SDKs with a common
  model interface, official MCP and Playwright bindings. These do not require
  adopting a large agent framework, an enterprise class hierarchy or Blazor.
- **Shared risk:** the actual subscription routes, native integrations and
  source-customisation workflow remain more consequential than empty-server
  benchmarks. A portable runtime is not a qualified three-OS application.

**Historical recommendation, superseded by the C# decision:** retain both as finalists. A slight Go
preference for backend/build simplicity remains reasonable, but do not select
it on presumed memory savings or presumed .NET complexity. C# is a genuine
alternative, not merely a Windows fallback. Qualify provider adapters and
packaging before choosing; avoid combining both backend languages without a
specific integration need.

## Concrete candidate stacks

| Responsibility | Go candidate | C# candidate |
| --- | --- | --- |
| Backend/API | `net/http`; add a router only for demonstrated benefit | ASP.NET Core Minimal APIs; no MVC/framework layering required |
| Model layer and optional loop | Fantasy `charm.land/fantasy` **v0.45.5**; official vendor clients are alternatives for specific gaps | `Microsoft.Extensions.AI` **10.11.0**; `IChatClient` adapters and optional `FunctionInvokingChatClient` |
| OpenAI / Anthropic | Fantasy adapters; official OpenAI/Anthropic Go SDKs if needed | Official OpenAI SDK through `Microsoft.Extensions.AI.OpenAI`; official `Anthropic` package exposes `AsIChatClient` |
| Local model servers | Compatible provider adapter, qualified per server/model | OpenAI-compatible transport or OllamaSharp's `IChatClient`; qualify tools/streams per model |
| External MCP tools | Official MCP Go SDK | Official `ModelContextProtocol.Core` client package; hosting/server packages only if needed |
| Website automation | Community Playwright Go, or Chromium-oriented chromedp/Rod | Official Microsoft.Playwright .NET |
| OAuth and credentials | `golang.org/x/oauth2`; `go-oidc` if needed; `go-keyring` candidate | OpenIddict client/system integration is a candidate; credential-store choice remains open |
| First UI | Same shared web-client assumption in both comparisons | Same shared web-client assumption in both comparisons |
| Later desktop host | Wails candidate | Photino.NET, Electron or Tauri candidate; thin WebView2 host covers Windows only |

These are comparison baselines, not a dependency shopping list. In particular,
do not adopt both raw SDKs and normalized adapters everywhere, or several desktop
hosts, merely to retain optionality. Shared web presentation does not select a
frontend framework. Blazor remains an alternative needing its own reuse and
responsiveness evidence.

## Findings that change the comparison

### Provider reuse is stronger than a README-only screen suggests

[Fantasy v0.45.5](https://github.com/charmbracelet/fantasy/tree/v0.45.5) exposes
normalized model/stream types and an optional tool loop. Its module requires
Go **1.27.2**; the probe used **1.27.3**. Its declared module graph includes many
provider/cloud dependencies, while the probe's actual import closure was smaller.
Neither the module-graph size nor the probe binary establishes Urd's final footprint.

**Do not misread its README's unsupported “image models” as no image input.**
The same pinned source has `FilePart`, image tool responses and provider mappings
for vision input. For example,
[`providers/anthropic/anthropic.go`](https://github.com/charmbracelet/fantasy/blob/v0.45.5/providers/anthropic/anthropic.go)
handles `image/*`, PDF and text file parts, and
[`providertests/image_upload_test.go`](https://github.com/charmbracelet/fantasy/blob/v0.45.5/providertests/image_upload_test.go)
contains image-input scenarios. These tests were inspected, **not run against
providers**. This establishes implementation paths, not successful Urd screenshot
handling across every route. The README also understates the inspected PDF path.

[Microsoft.Extensions.AI](https://learn.microsoft.com/en-us/dotnet/ai/microsoft-extensions-ai)
is not just transport types: its optional `FunctionInvokingChatClient` actually
runs model → function → model iterations. The probe exercised that implementation,
not a substitute Urd loop. It does not require Semantic Kernel or Microsoft Agent
Framework.

The official [Anthropic C# SDK](https://github.com/anthropics/anthropic-sdk-csharp)
**13.4.0** includes `AsIChatClient` in its main `Anthropic` package; the adapter
was found in `src/Anthropic/AnthropicClientExtensions.cs`, despite its absence
from the README walkthrough. Source supports streaming and image/PDF mappings.
Do not budget a mandatory custom Claude normalization adapter on the mistaken
assumption that only community MEAI adapters exist. This adapter was source-reviewed,
not exercised by the fake-model experiment. See
[the integration issue](https://github.com/anthropics/anthropic-sdk-csharp/issues/4)
and [published package](https://www.nuget.org/packages/Anthropic/13.4.0).

### Subscription access is unresolved for both, not a C#-specific defect

Generic OAuth, a configurable bearer token and an SDK's provider list do not
establish subscription entitlement or exact request compatibility.
[PROVIDER-RESEARCH.md](PROVIDER-RESEARCH.md) records the ChatGPT sign-in/Responses
route and remaining raw Copilot qualification gap; Claude stays API-only.
Neither experiment used accounts, credentials or real model requests.

Go's [OAuth package](https://pkg.go.dev/golang.org/x/oauth2) and
[OpenIddict's client integration](https://documentation.openiddict.com/integrations/operating-systems)
can save standard flow plumbing. Registration, provider-specific parameters,
refresh behaviour and model routing still need qualification. OpenIddict brings
its own hosting/integration conventions; it is not automatically the smallest
choice. [go-keyring](https://github.com/zalando/go-keyring) offers native-store
access on all three OS families, but Linux depends on Secret Service/D-Bus.
A headless backend and a signed-in desktop may have different credential-store
availability. No keystore was selected or accessed in these experiments.

### MCP is available in both; browser dependencies are not equal to backend dependencies

Both have [official MCP SDKs](https://modelcontextprotocol.io/docs/2026-07-28/sdk).
The [Go SDK](https://github.com/modelcontextprotocol/go-sdk) supplies client,
transport and auth primitives; the
[C# SDK](https://github.com/modelcontextprotocol/csharp-sdk) separates the core
client from optional hosting/server integrations. Pin versions and use their
matching protocol/migration documentation. Availability is source evidence;
MCP execution and cancellation were not tested here.

[Playwright .NET](https://github.com/microsoft/playwright-dotnet) is official;
[Playwright Go](https://github.com/playwright-community/playwright-go) is a
community binding. **Both use a Node-based driver plus browser processes.**
Neither makes the entire automation stack a single native backend executable.
[chromedp](https://github.com/chromedp/chromedp) and
[Rod](https://github.com/go-rod/rod) offer Go/CDP alternatives without that
Playwright driver, but narrow coverage to Chrome/Chromium-style targets. Browser
coverage is not yet a reason to silently replace retained automation behaviour.
Desktop control remains separate and requires OS-specific implementation in both
languages; no cross-OS automation package was qualified.

### Packaging: Go has a credible advantage, not a blanket “one binary” promise

The tested Go loop built with `CGO_ENABLED=0` and no C compiler. A backend release
can avoid a separately installed Go runtime, but users changing source still
need the Go SDK and any frontend build tools. Native dependencies and browser
helpers can change the complete package.

[Self-contained .NET JIT publishing](https://learn.microsoft.com/en-us/dotnet/core/deploying/)
bundles the runtime; users running it need not install .NET separately. Users
building modifications need the SDK. Single-file packaging and Native AOT are
separate choices. **Start comparison with ordinary JIT, not AOT requirements:**
[Native AOT](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/)
adds native build prerequisites and compatibility restrictions. This is a
recommended baseline, not a permanent prohibition on AOT. Neither a release
package nor a clean-machine source build was produced in this round.

[Wails v3 installation](https://v3.wails.io/quick-start/installation/) provides a
same-language host path for Go, but the
[platform build requirements](https://v3.wails.io/getting-started/installation/)
include native tooling/webview dependencies. Its documented Linux targets are
not proof of every Linux distribution/display configuration. Do not extrapolate
the pure-Go backend probe's prerequisites to the desktop build.

[Photino.NET](https://github.com/tryphotino/photino.NET) provides a .NET wrapper
around platform webviews across Windows/macOS/Linux; it deserves a host probe,
not an unqualified maintenance or footprint endorsement. Electron and Tauri can
also connect to either backend, with their respective runtime/toolchain costs.
[MAUI](https://learn.microsoft.com/en-us/dotnet/maui/supported-platforms) does not
supply the Linux desktop target; WebView2 alone does not supply macOS/Linux.
Browser-first delivery does not require settling the desktop host now.

### Cancellation plumbing is useful, but application lifetime still belongs to Urd

Go contexts and .NET cancellation tokens reach the tested library paths. They
are cooperative mechanisms, not arbitrary-code termination or rollback.
[`exec.CommandContext`](https://pkg.go.dev/os/exec#CommandContext) defaults to
killing the command process, not guaranteeing descendant-tree cleanup.
[`.NET Process.Kill(entireProcessTree: true)`](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.process.kill)
offers tree termination, but `WaitForExit`/`HasExited` do not confirm every
descendant has exited. Shell cancellation needs its own OS tests.

**A disconnected browser must not cancel background agent work.** Request abort
signals are convenient for request-scoped work, not automatically the agent's
lifetime token. This follows Urd's existing backend/client lifetime decision,
regardless of language. No HTTP/UI disconnect behaviour was tested here.

## Small experiments: matched behaviours, not performance benchmarks

Real published libraries ran against fake implementations of their public model
interfaces. All provider responses were scripted in-process. Harmless echo/count
functions were real library-invoked tools. No HTTP server, UI, storage engine,
MCP server, browser, real provider or desktop session was involved.

| Behaviour | Go / Fantasy v0.45.5 | C# / MEAI 10.11.0 |
| --- | --- | --- |
| Streaming model → echo tool → final model text | Passed: model twice, tool once, matching call/result identity | Passed: model twice, tool once, matching call/result identity |
| Cancel while model awaits output | Passed: `context.Canceled`, no tool effect | Passed: `TaskCanceledException`, no tool effect |
| Cancel during a cooperative slow tool | Passed: tool exits; no second model request | Passed: `OperationCanceledException`; no second model request |
| Fail simulated save after tool completion, before next model request | Passed using awaited `PrepareStep`; first tool already ran | Passed using caller-owned `DelegatingChatClient` between loop and model; completed call/result reached gate, first tool already ran |

The fourth case deliberately does **not** demonstrate durability or a mandatory
Urd persistence barrier. It asks whether the application can stop the next model
request on a control failure without patching the library. Fantasy supplies a
direct hook; MEAI's public middleware composition supplies the necessary seam.
Both preserve the distinction between a stopped continuation and an already
performed effect. Neither proves every observer callback is a reliable gate.

Each final suite passed twice; parent verification then reran both, built C# with
`--no-restore`, and ran Go vet. Independent review checked source/reports and
prompted corrections to the initial C# gate placement and provenance descriptions.
Go's toolchain was **1.27.3**; .NET SDK **10.0.403**, runtime **10.0.14**. No new
machine-wide toolchain installation or application activation was performed.
Initial .NET execution populated ordinary user-level first-use/cache files;
subsequent commands isolated CLI/HTTP caches and disabled dev-certificate creation.
An initial claim that these probes created a certificate was checked and withdrawn:
the existing certificate predates the probes; no certificate was modified.

Workers ran concurrently, so no comparative timings or memory readings are
reported. Source size includes different test scaffolding and is not a measure
of product LOC. Cancellation bounds are test timeouts, not latency benchmarks.

## Evidence, reproduction and remaining qualification

Artifacts: `%TEMP%\urd-go-dotnet-discovery-73fef49c\`:

- `BRIEF.md` defines the shared scope; `verify.mjs` repeats parent checks.
- `go/`: `probe_test.go`, `go.mod`, `go.sum`, `REPORT.md`, two run logs,
  dependency/import-closure evidence and an isolated module cache.
- `dotnet/`: `probes/Program.cs`, project-level `packages.lock.json`,
  `REPORT.md`, two run logs and an isolated package cache. Report records a
  lock-content-hash versus downloaded-package-hash discrepancy without claiming
  they are identical; exact package versions and assembly were checked.
- `parent-*.log` records independent local reruns.

Temporary artifacts are not a durable source archive. This document preserves
findings and limits; source links outside the Fantasy pin may move. Research
used primary package documentation/source; a package's existence is not a
compatibility, licensing-combination or maintenance guarantee.

The next discriminating work is **not another language microbenchmark**:

1. Exercise actual chosen provider adapters against local scripted HTTP/SSE,
   including tool calls, screenshots, terminal errors and usage. Separately
   qualify required subscription route contracts; fake transport cannot prove
   account entitlement.
2. Rehearse the chosen dependency set's release and user-source-build workflow,
   starting on Windows while checking concrete macOS/Linux prerequisites.
3. Only then measure a representative session/history/UI workload and whole
   process tree. UI tab responsiveness, fan-out and sustained memory remain
   unmeasured by these probes.

These are recommended next gates, not permission to sign in, spend provider
credits, install desktop tooling, select storage or build a full Urd prototype.
