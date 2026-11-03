# First-pass foundation ranking

> **Subsequent decision (2026-11-03):** C#/.NET is selected for the backend.
> This ranking is historical research, not a continuing backend selection process.
> See [the agreed decision](ARCHITECTURE.md#c-backend-and-class-design).

## Status and requirements sufficiency

Research date: 2026-11-03. Seven parallel investigations covered JS runtimes,
Go/Rust, .NET, JVM/BEAM, Python/Ruby/PHP, other native-language candidates, and
shared UI/desktop hosting. Findings were consolidated and cross-lane assumptions
reviewed. This is **a provisional ranking for further investigation, not a stack
selection or performance result**. No toolchains were installed, prototypes
built, accounts accessed or resource benchmarks run in this round.

**We have enough requirements to rank structural fit, not enough evidence to
choose a final winner.** [README.md](README.md) and [ARCHITECTURE.md](ARCHITECTURE.md)
cover the important direction: small maintained code, low resource overhead,
browser-first delivery with an OS rollout of Windows → macOS → Linux,
one shared backend with independent client
lifetimes, source-bundled local customisation, provider access, delegation,
pruning, history/lifecycle rules, browser automation and desktop control.
Minimising duplication across browser, desktop and VS Code is now explicit.

Still open: numerical resource/latency expectations, representative workloads,
full feature discovery, exact provider-route compatibility, package/native
integration behaviour, actual local-build friction and owned-code size. Storage
and model/tool loop ownership remain undecided. These gaps do not prevent a
first screen; they prevent declaring an overall production-ready winner.

The full [candidate inventory](FOUNDATION-CANDIDATES.md) remains open. Frameworks
were grouped and representative combinations checked; **not every named package
received equal-depth investigation or an individual score**. An unverified option
is not automatically unsuitable.

## Portability correction: Windows → macOS → Linux

The owner clarified this intended rollout after the initial ranking. The initial
screen treated other OS targets as unspecified and overemphasised Windows fit.
**Existing score profiles and bands are a Windows-first baseline, not a completed
three-OS ranking.** Cross-platform runtime support alone cannot qualify the full
application. No numerical re-scoring has been invented without that investigation.

Consequences for the comparison:

- **C#/.NET remains a cross-platform backend candidate.** Do not confuse .NET
  runtime coverage with Windows-only WPF/WebView2 hosts or UI Automation APIs.
  MAUI's official targets include macOS via Mac Catalyst but not Linux; it does
  not by itself cover the intended desktop rollout. A shared web UI with another
  host remains possible without replacing the .NET backend.
- **Electron and Tauri document Windows/macOS/Linux desktop targets.** That gives
  them a relevant coverage advantage over a Windows-only host, not an automatic
  resource or maintenance win. Tauri uses different platform webviews; renderer
  behaviour and platform build prerequisites still need checking. Wails,
  Neutralino and other hosts need the same complete-stack comparison.
- **A thin WebView2-only host is a Windows-stage option, not the whole desktop
  strategy.** Count later host adapters or replacement work explicitly. Native
  Windows integration should not receive unqualified long-term-fit credit.
- **Reassess every backend's dependency graph**, including native modules, browser
  drivers, credentials, shell/process cancellation, tray/background controls and
  desktop automation. A browser UI on a Mac connected to a Windows backend is
  not evidence that Urd runs on macOS; execution remains on the backend machine.
- **Assess release and source-build paths on each OS**, including macOS signing/
  notarisation and permission workflows where relevant, and Linux distribution,
  packaging and X11/Wayland differences. These are qualification topics, not newly
  selected distribution policies or promises of identical automation permissions.

The shared-UI recommendation still stands. The leading backend group remains a
set of candidates, but its ordering and host recommendations need reassessment
against all three OS targets before selection. Add platform-specific glue,
replacement work and ongoing test/build costs to O/C/I/M; S must consider reuse
across operating systems as well as interface surfaces. R remains unknown.
This requires early portability investigation, not implementing all OS support now.

Sources: [.NET overview](https://learn.microsoft.com/en-us/dotnet/core/introduction),
[WPF](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/overview/),
[WebView2 distribution](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/distribution),
[MAUI supported platforms](https://learn.microsoft.com/en-us/dotnet/maui/supported-platforms),
[Electron introduction](https://www.electronjs.org/docs/latest/),
[Tauri prerequisites](https://v2.tauri.app/start/prerequisites/) and
[platform webviews](https://v2.tauri.app/reference/webview-versions/).

## Method: score profiles, then ranked bands

Scores below are provisional ordinal judgments from the initial Windows-first
screen, not measurements or revised three-OS scores. Future assessment must extend
these axes to the full rollout described above:

| Axis | What is assessed |
| --- | --- |
| O — owned implementation | Amount of application-specific glue and architectural machinery suggested by the documented integration paths; not measured LOC. |
| S — surface sharing | Reuse of actual UI components/presentation and backend behaviour across browser, desktop and VS Code; not simply using the same language. |
| C — customisation | User-machine source/dependency/build/test/reload and release prerequisites; not measured build speed. |
| I — integration | Documented paths for Urd's required model/tool, lifecycle, Windows and client integrations; not completed qualification. |
| M — maintenance | Dependency/adoption scope and compatibility/support risks; not a claim about measured future maintenance effort. |
| R — resources | Runtime CPU/RAM and click-to-content responsiveness under representative work, including retained data and helper processes. |

Scale: **4** strong documented structural fit; **3** plausible with bounded work;
**2** material integration/complexity questions; **1** substantial apparent burden;
**0** documented conflict with the assessed role; **U** insufficient evidence.
Every R is **U**. Missing observations are neither zeros nor perfect scores.

The common worker brief proposed weights O20/S20/C15/I15/M10/R20. Those are analyst
weights, **not owner decisions**. No weighted overall score is published: resource
performance is entirely unmeasured, other scores are coarse judgments, and a
required-capability gap must not be averaged away. Ranked bands reflect both fit
and evidence confidence. Ties are deliberate; small score differences are not a
basis for elimination.

### Cross-lane calibration

- Backend rows below use the **same shared web-client assumption**, so S=4 across
  languages. This is structural reuse potential, not proof that host integration
  is finished. Backend/API contract glue belongs in O/I, not an invented second UI.
- Go's local-build score is normalised to C=3 alongside other ordinary SDK builds:
  a native release does not remove its source SDK, client toolchain or dependency
  qualification. Bun's C=4 credits its documented integrated CLI option, not speed.
- C#/F#/Scala/Clojure can reuse their runtime ecosystems. A lack of a separate
  language-branded AI SDK does not mean hand-writing every provider integration.
- Server-rendered and WASM UIs can also reuse components across webview surfaces.
  Connection dependence, runtime assets and host compatibility are separate costs.
- Generic OAuth/provider SDK support is not subscription entitlement. The unresolved
  standalone Copilot route affects **every** candidate. Native addons/NIFs/wheels
  require source compilers only when the actual dependency/build path needs them.
- Framework breadth, a pre-1.0 version or lack of an official SDK alone does not
  demonstrate a violation of Urd's requirements. Focused adoption remains eligible.

## Backend ranking

### Band A — strongest current qualification candidates

These are a leading group, **not a defensible numbered ordering within it**.
Scores assume a shared web client; UI alternatives are assessed separately.

| Concrete candidate | O/S/C/I/M/R | Confidence | Main advantage | Main cost or unresolved issue |
| --- | --- | --- | --- | --- |
| **TypeScript + supported Node LTS + Hono or Fastify + Vercel AI SDK** | 3/4/3/3/3/U | Medium-high for source availability; integration incomplete | Broad relevant libraries; shared JS/TS tooling with browser and extension; existing narrow offline SDK control probes. | Native desktop/tray integration and packaged-source workflow still need work; experimental SDK controls need care. |
| **C# + .NET self-contained JIT + ASP.NET Core Minimal APIs + Microsoft.Extensions.AI** | 3/4/3/3/3/U | Medium-high | Focused model abstraction plus documented Windows APIs; no need to adopt a complete agent framework. | Shared JS UI adds a toolchain; Blazor is an alternative, not automatic proof of less total machinery. Browser driver and UIA/publish profile require checks. |
| **Go + net/http or Chi + Fantasy** | 3/4/3/3/3/U | Medium | Native distribution; focused normalised provider API and optional loop; standard context/cancellation primitives. | Windows control and required provider routes remain unqualified; JS client still needs its toolchain; browser integration may launch Node. |
| **CPython + FastAPI/Starlette + PydanticAI** | 3/4/3/3/3/U | Medium | Typed model/tool libraries, official MCP ecosystem and a source-editable backend. | Interpreter/environment and native-wheel packaging; exact loop controls, local build and long-running workload behaviour need testing. No speed penalty inferred from language reputation. |
| **TypeScript + Bun ordinary JIT/standalone build + Hono + AI SDK** | 3/4/4/2/2/U | Medium-low | Ordinary executable can expose the Bun build/package CLI, particularly relevant to bundled-source customisation. | Exact Windows SDK, subprocess, browser and native-addon compatibility is unqualified; the maintenance score reflects that extra compatibility surface, not release frequency alone. |
| **Kotlin + Ktor + LangChain4j or a separately assessed Koog loop** | 3/4/3/3/2/U | Medium | Focused server option, reusable JVM model/MCP libraries, bundled-runtime distribution. | Client tooling and JVM/native integration seams; specific adapter maturity and packaging combinations need qualification. No JVM footprint assumption. |

Model-client and managed-loop variants remain separate experiments. Raw vendor
clients are eligible but may shift normalisation into Urd. None of these rows
requires Urd to hand-write a loop or surrender session/task ownership to an SDK.

### Band B — credible alternatives needing more targeted evidence

These are lower investigation priorities, not rejected languages. Equal profiles
can appear in different bands because coverage/confidence differs.

| Candidate | O/S/C/I/M/R | Why keep it; what needs resolution |
| --- | --- | --- |
| **Deno + Hono + the shared TS model layer** | 3/4/3/2/2/U | Integrated TS tooling and standalone builds. General Node/npm compatibility caveats exist, but no Urd-required package failure was demonstrated. Include in the portable TS compatibility check. |
| **Rust + Axum/Tokio + genai or Rig** | 2/4/2/2/2/U | Credible focused libraries and explicit task/Windows API options. Default Windows MSVC build prerequisites and browser/provider integration need qualification; no resource superiority assumed. |
| **F# + Giraffe/ASP.NET + .NET model libraries** | 3/4/3/2/2/U | Reuses .NET capabilities; Fable/Bolero are UI alternatives. Idiomatic integration/build costs were less deeply checked than C#. |
| **Java/Kotlin + Spring Boot/Spring AI or Quarkus/LangChain4j** | 2–3/4/3/3/2/U | Broad integration facilities may save owned code. Additional framework conventions need to earn their place; broad APIs are not inherently disqualifying. Native Image is a separate publish choice. |
| **Clojure or Scala + JVM server/model libraries** | 3/4/3/2–3/2/U | Java interop makes model/MCP libraries available. Concrete idiomatic wrappers, lifecycle and source-build paths received less qualification, not a proven ecosystem deficit. |
| **Dart + Shelf or Dart Frog + focused clients/langchain_dart** | 3/4/3/2/2/U | Documented Windows SDK/native-build path and optional shared Dart UI. Exact MCP, desktop-control and provider-route paths remain unverified; community libraries/helpers remain possible. |
| **Elixir + Plug/Bandit + Elixir LangChain; Phoenix optional** | 3/4/3/2/2/U | Actor/task facilities and a focused server path; LiveView may share UI. Model/MCP/desktop integration needs checking; native compiler burden depends on selected NIFs, not all Elixir apps. |
| **Ruby + Sinatra/Puma + RubyLLM** | 3/4/2/2/2/U | Compact server and model/tool facilities. Windows native-gem/build paths and background cancellation/coordination need qualification. |
| **PHP + Slim/FrankenPHP + optional Neuron AI** | 3/4/3/2/2/U | Native Windows resident-worker path and source-editable app. Request/worker lifetime does not establish concurrent background-agent ownership; this is the first integration gate. |

PyPy, free-threaded CPython and JRuby are separate runtime variants, not automatic
upgrades. Their dependency compatibility and resource behaviour remain unscored.
A ClojureScript, Fable, Elm or Gleam client is also a separate UI/toolchain choice,
not a requirement imposed by the backend language.

### Band C — lower priority or insufficient evidence for this role

- **C/C++ servers:** CivetWeb, libmicrohttpd, Drogon, Crow, oat++ and Boost.Beast
  provide relevant HTTP/server building blocks. This screen did not establish a
  comparably integrated Urd model/tool stack. That is an integration-evidence gap,
  not proof community libraries do not exist or that all code must be handwritten.
  No supported resource gain currently offsets that uncertainty. No overall score.
- **Swift/Vapor/Hummingbird:** Swift compiler availability on Windows does not
  qualify these server frameworks. The reviewed sources did not establish native
  Windows support for Vapor or Hummingbird. A WSL/container fallback would add
  user-machine setup, but that fallback was not qualified here. Mandatory WSL is
  a material burden, not an owner-imposed blanket prohibition.
- **Zig:** Zap documents no native Windows support. http.zig documents a different
  blocking Windows serving model; std HTTP and the Baz successor remain insufficiently
  assessed. No claim that concurrency is impossible or that Zig cannot fit Urd.
- **Gleam/Wisp/Mist and raw Erlang/Cowboy:** plausible server components, but this
  screen did not establish the complete Windows/provider/tool-library combination.
- **Unexamined framework variants:** insufficient evidence, not last place by
  default. Helidon/Micronaut/Pedestal details, individual renderer variants and
  alternative native/community SDKs need targeted work if advanced.

## UI ranking: the strongest architectural result

**First investigate one shared UI with narrow host adapters.** Do not implement
three presentations merely because there are three delivery surfaces. A shared
language helps tooling but is neither necessary nor sufficient for component reuse.
The backend must outlive client closure; that does not itself mandate a permanently
separate OS process or a general process-management framework.

| Priority / architecture | O/S/C/I/M/R | Pros and costs |
| --- | --- | --- |
| **Advance: one TS/DOM client** using React, Vue, Svelte, Solid, Preact, Angular or Lit | 3/4/4/3/3/U | Same components/state can serve browser, desktop webview and VS Code. Still needs host API bridges, asset/resource mapping, authentication/transport and CSP handling. No evidence yet ranks these component frameworks individually. |
| **Reserve: Blazor components**, server-interactive or WASM, with optional Blazor Hybrid host | 3/4/2/2/3/U | Real Razor-component reuse. Server mode has circuit/connection behaviour; WASM has client runtime/build assets. VS Code bridge and actual source workflow remain to qualify. C may improve when .NET tooling is already required by the backend. |
| **Reserve: server-driven HTML**, e.g. LiveView, htmx/Hotwire, Livewire, Vaadin | 3/4/3/2/3/U | Can reuse templates/UI in all webview surfaces and reduce client code. Connection behaviour, client state and backend integration need testing. No offline-client requirement has been invented to exclude it. |
| **Reserve: Rust web components or Flutter web/desktop** | 2/4/2/2/2–3/U | Actual shared component/widget source is possible. Qualify webview assets, bridge, text/selection/accessibility, target parity and build toolchain rather than assuming duplicate UI. |
| **Reserve: cross-target toolkits**, e.g. Avalonia Browser or Qt WebAssembly | 2/4/2/2/2/U | Browser targets can reuse native-target component source. Browser/WASM delivery and VS Code host behaviour need checking. Compose/Slint web remain less-verified inventory leads. |
| **Poor fit as a second complete UI: platform-only native widgets** | 2/1/2/2/3/U | Standalone WPF/WinUI/WinForms/JavaFX/Swing presentation requires another browser presentation. This penalty does NOT apply to using them only as thin webview hosts or to toolkits with shared browser targets. |

Full-stack JS frameworks (Next/Nuxt/SvelteKit/SolidStart/Astro) remain eligible,
including client/static modes. They need not require another runtime server;
conversely, adding a second UI server should earn its cost. Do not build a generic
cross-framework component abstraction just to keep every renderer interchangeable.
Lustre is a client-rendering option, not simply another server-driven UI.

### Desktop hosts: conditional fit, not an absolute ranking

| Host | Why investigate it | Additional boundary to count |
| --- | --- | --- |
| Electron | JS/TS host/tooling aligned with a TS application | Bundled Chromium/Node architecture; renderer/preload/main-process boundaries and background lifetime. |
| Tauri | Rust host around shared web assets | Rust build prerequisites, platform webview and command bridge; may align with a Rust backend. |
| Wails | Go host around shared web assets | Go/native bindings and host lifecycle; may align with a Go backend. |
| WebView2 with a thin host | Direct Windows web hosting; can align with a .NET backend | Windows-stage host only, not the macOS/Linux strategy; count additional host implementations, distribution and messaging. |
| Neutralinojs | Another webview shell with a native API bridge | Actual packaging, native capabilities, local-build path and bridge fit need checking. |

No host is a proven RAM winner. The right ordering depends partly on the selected
backend/build toolchain. A thin WPF host with shared web content is not the same
as rebuilding Urd's presentation in WPF. A native shell can connect to the same
backend without embedding a second agent engine.

## Framework and packaging disposition

Use minimal examples as comparison baselines, **not bans on larger frameworks**:
Hono/Fastify/Express; Go standard HTTP/Chi; Axum with Actix/Rocket/Poem alternatives;
ASP.NET Minimal APIs; FastAPI/Starlette; Ktor; Shelf; Plug/Bandit; Sinatra/Puma; Slim.
Gin/Echo/Fiber, FastEndpoints/MVC/Giraffe/Saturn, NestJS/Elysia, Litestar/aiohttp,
Django/Flask, Rails/Roda/Hanami, Symfony/Laravel and Serverpod remain alternatives.
Their conventions, async/worker model, maintenance and dependency graph must be
checked for the particular Urd role; this pass did not measure their owned LOC.

The same applies to model libraries. Microsoft Agent Framework/Semantic Kernel,
OpenAI Agents SDK, LangChain/LangGraph, Mastra, Google ADK, Koog, LlamaIndex,
Haystack, smolagents, RubyLLM/langchainrb and Prism/Neuron remain eligible for
focused comparison. Optional checkpointing, graph or server modules are not
necessarily mandatory. Do not inherit competing lifecycle stores casually, but
do not equate a library loop with the excluded complete agent applications.

Packaging must be assessed separately from language choice:

- Bundled Node plus source is distinct from Node SEA. Bun's ordinary executable
  can expose its CLI; experimental Bun AOT cannot. A separate build CLI could
  still accompany an AOT release, so this is not an automatic customisation ban.
- .NET self-contained JIT is distinct from Native AOT and from single-file
  publishing. Windows AOT adds C++ prerequisites and lacks built-in COM; explicit
  interop alternatives may work but need qualification. No automatic AOT win.
- JVM app images can include a runtime. Installer formats and GraalVM Native Image
  add separate prerequisites; do not charge them to every JIT build.
- Native wheels/gems/NIFs/addons and browser/driver binaries belong in the actual
  graph. A compiled backend can still launch Node through browser bindings.
- Source presence, one executable, build speed and low running RAM are four
  different properties. Guidance changes need not imply a full application build.

## What to investigate next

The approved [small-operation experiment proposal](EXPERIMENT-PROPOSAL.md) covers
TypeScript, Python, Kotlin, Rust, Go and C#, with Bun/Deno runtime checks. Its
[Windows first-round observations](EXPERIMENT-RESULTS.md) are recorded separately:
basic CLI data operations and source changes, not a miniature Urd. The larger
integration scenarios below remain deferred; these results do not replace later
integration or three-OS qualification.

A subsequent [Go/C# discovery round](GO-DOTNET-DISCOVERY.md) checked concrete
libraries and ran matched fake-model streaming/tool/cancellation probes against
Fantasy and Microsoft.Extensions.AI. Both passed; Fantasy offers a direct
step-control hook, while the .NET probe used public middleware composition.
The report also corrects README-only assumptions about Fantasy image input and
the official Anthropic C# adapter. It does not select a stack or establish a
resource winner, real-provider compatibility or three-OS qualification.

**Recommended test order, not a new stack commitment:** First audit the intended
Windows → macOS → Linux path for each leading stack's dependencies, hosts,
packaging and local builds. Do this before selection; the Windows-first fixtures
below cannot establish three-OS suitability.

1. Prove one representative session view can be shared across browser, VS Code
   webview and one desktop host. Include streaming, paged history, tool inspection,
   a host action and client-close/background-work behaviour. Count actual adapters
   and duplicated presentation; keep UI and agent work lifetimes separate.
2. Use a common small offline model/tool fixture to compare the leading backends.
   Node/Bun, Go and .NET are a useful initial contrast; Python and Kotlin remain
   credible challengers, not eliminations. Include Deno in the common TS runtime
   compatibility pass rather than assuming failure. This is work ordering only.
3. Rehearse clean user-machine source edits/builds and actual packaging. Qualify
   Windows control, browser driver, MCP, streaming, tool admission/cancellation,
   human input and steering before treating any SDK as the selected loop.
4. Measure runtime resources and click-to-visible-content using matched data and
   activity mixes, with process trees and build costs reported separately. Establish
   practical acceptance expectations before declaring a performance pass/fail.

No benchmark or visible desktop-control permission is implied by this research.
Provider entitlement and live authentication need their own qualification; mocks
cannot establish them. The concrete cases in [FOUNDATION-RESEARCH.md](FOUNDATION-RESEARCH.md)
and the data lifetimes in [STORAGE-RESEARCH.md](STORAGE-RESEARCH.md) remain inputs.

## Primary evidence and provenance

Sources accessed 2026-11-03; moving documentation and version-specific combinations
must be rechecked before implementation. The seven working reports are under
`%TEMP%\urd-ranking-ec92f5ef\` (`js.md`, `go-rust.md`, `dotnet.md`, `jvm-beam.md`,
`dynamic.md`, `other-native.md`, `shared-ui.md`). They are intermediate research;
this document preserves the calibrated conclusions and supersedes conflicting
raw scores or over-broad statements. Temporary report survival is not required.

- JS/runtime: [Hono Node adapter](https://hono.dev/docs/getting-started/nodejs),
  [Node release support](https://github.com/nodejs/Release),
  [Node SEA](https://nodejs.org/api/single-executable-applications.html),
  [Bun ordinary executable/CLI](https://bun.com/docs/compiler/executables),
  [Bun AOT](https://bun.com/docs/compiler/aot),
  [Deno Node APIs](https://docs.deno.com/runtime/reference/node_apis/),
  [Deno compile](https://docs.deno.com/runtime/reference/cli/compile/).
- Go/Rust: [Go request context](https://pkg.go.dev/net/http#Request.Context),
  [Fantasy](https://github.com/charmbracelet/fantasy),
  [Axum](https://docs.rs/axum/latest/axum/), [genai](https://github.com/jeremychone/rust-genai),
  [Rig](https://github.com/0xPlaygrounds/rig),
  [Rust Windows prerequisites](https://rust-lang.github.io/rustup/installation/windows-msvc.html).
- .NET: [publishing](https://learn.microsoft.com/en-us/dotnet/core/deploying/),
  [Native AOT](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/),
  [Microsoft.Extensions.AI](https://learn.microsoft.com/en-us/dotnet/ai/microsoft-extensions-ai),
  [reusing Razor components across hosts](https://learn.microsoft.com/en-us/aspnet/core/blazor/hybrid/reuse-razor-components?view=aspnetcore-10.0).
- JVM/BEAM: [Ktor](https://ktor.io/docs/server-create-a-new-project.html),
  [LangChain4j](https://docs.langchain4j.dev/), [Koog](https://docs.koog.ai/),
  [jpackage](https://docs.oracle.com/en/java/javase/26/jpackage/packaging-overview.html),
  [Clojure Java interop](https://clojure.org/reference/java_interop),
  [Scala Java interop](https://docs.scala-lang.org/tutorials/scala-for-java-programmers.html),
  [Mix releases](https://hexdocs.pm/mix/Mix.Tasks.Release.html),
  [Bandit](https://github.com/mtrudel/bandit), [Elixir LangChain](https://hex.pm/packages/langchain).
- Python/Ruby/PHP: [Windows asyncio](https://docs.python.org/3/library/asyncio-platforms.html),
  [PydanticAI](https://ai.pydantic.dev/agent/), [FastAPI](https://fastapi.tiangolo.com/),
  [RubyInstaller DevKit](https://rubyinstaller.org/add-ons/devkit.html),
  [RubyLLM tools](https://rubyllm.com/tools/),
  [FrankenPHP Windows deployment](https://frankenphp.dev/docs/production/),
  [resident-worker semantics](https://frankenphp.dev/docs/worker/).
- Other backends: [Dart compilation](https://dart.dev/tools/dart-compile),
  [LangChain.dart](https://pub.dev/packages/langchain),
  [Vapor Windows tracking](https://github.com/vapor/vapor/issues/2646),
  [Swift server packages](https://www.swift.org/packages/server.html),
  [Drogon installation](https://github.com/drogonframework/drogon/wiki/ENG-02-Installation),
  [Zap](https://github.com/zigzap/zap), [http.zig](https://github.com/karlseguin/http.zig).
- UI/hosts: [VS Code webviews](https://code.visualstudio.com/api/extension-guides/webview),
  [Blazor render modes](https://learn.microsoft.com/en-us/aspnet/core/blazor/components/render-modes?view=aspnetcore-10.0),
  [LiveView](https://hexdocs.pm/phoenix_live_view/), [htmx](https://htmx.org/docs/),
  [Flutter web/accessibility](https://docs.flutter.dev/ui/accessibility/web-accessibility),
  [Avalonia WebAssembly](https://docs.avaloniaui.net/docs/guides/platforms/how-to-use-web-assembly),
  [Qt WebAssembly](https://doc.qt.io/qt-6/wasm.html),
  [Electron process model](https://www.electronjs.org/docs/latest/tutorial/process-model),
  [Tauri process model](https://v2.tauri.app/concept/process-model/),
  [Wails architecture](https://v2.wails.io/docs/howdoesitwork/),
  [Neutralino architecture](https://neutralino.js.org/docs/contributing/architecture/),
  [WebView2](https://learn.microsoft.com/en-us/microsoft-edge/webview2/).
