# Foundation candidate inventory

## Purpose and scope

An expanded, **unranked research list**, requested on 2026-11-03. This is an
inventory only: no scores, recommendations, eliminations or stack selections.
Inclusion does not establish Windows support, maintenance status, licensing fit,
provider coverage, local-build feasibility or acceptable resource use.

Later investigation will compare concrete combinations against Urd's documented
requirements and the useful capabilities carried forward from Pie, rather than
requiring parity with Pie's implementation. See [README.md](README.md) and
[ARCHITECTURE.md](ARCHITECTURE.md). The earlier proposed research order in
[FOUNDATION-RESEARCH.md](FOUNDATION-RESEARCH.md) does not restrict this inventory.

Languages, runtimes, model libraries, server frameworks and UI frameworks solve
different parts of the problem. Keep their evaluations separate, then assess
complete combinations. A language need not adopt every framework listed beside
it. A standard-library implementation with a few focused dependencies is also a
candidate; using a comprehensive framework is not mandatory.

## 1. Backend languages, runtimes and server frameworks

Framework names are examples to investigate within each ecosystem, not required
pairings or claims that every pairing supports the same platforms.

| Language family | Runtime/toolchain candidates | Server/framework candidates |
| --- | --- | --- |
| JavaScript / TypeScript | Node.js; Bun; Deno | Standard HTTP APIs; Express; Fastify; Hono; NestJS; Elysia |
| Go | Go toolchain | `net/http`; Chi; Gin; Echo; Fiber |
| Rust | Rust/Cargo | Axum; Actix Web; Rocket; Poem |
| C# | .NET; self-contained JIT; Native AOT | ASP.NET Core Minimal APIs; ASP.NET Core MVC; FastEndpoints |
| F# | .NET | ASP.NET Core; Giraffe; Saturn |
| Python | CPython; PyPy; free-threaded CPython as a separate variant | FastAPI; Starlette; Litestar; Django; Flask; aiohttp |
| Java | JVM distributions; GraalVM Native Image | Spring Boot; Quarkus; Micronaut; Vert.x; Helidon |
| Kotlin | JVM; Kotlin Multiplatform/Native where applicable | Ktor; Spring Boot |
| Scala | JVM; Scala Native where applicable | Pekko HTTP; http4s; Play Framework |
| Clojure | JVM | Ring; Reitit; Pedestal |
| Elixir | BEAM / Erlang OTP | Phoenix; Plug; Bandit |
| Erlang | BEAM / OTP | Cowboy; OTP applications |
| Gleam | Erlang target; JavaScript target where applicable | Wisp; Mist |
| Dart | Dart VM; native compilation | Shelf; Dart Frog; Serverpod |
| Swift | Swift toolchain | Vapor; Hummingbird |
| Ruby | CRuby; JRuby | Rails; Sinatra; Hanami; Roda |
| PHP | PHP; long-running hosting variants such as RoadRunner or FrankenPHP | Laravel; Symfony; Slim |
| C | Native C toolchains | CivetWeb; libmicrohttpd; focused native libraries |
| C++ | Native C++ toolchains | Drogon; Crow; oat++; Boost.Beast |
| Zig | Zig toolchain | httpz; Zap; standard-library/focused-library approach |

Listing an alternative runtime or compilation target does not imply the adjacent
frameworks run on it. Those combinations need explicit verification later.

## 2. Model/provider, tool-loop and orchestration libraries

These are possible dependencies, not ready-made replacements for Urd. Evaluate
model-only and optional loop/orchestration facilities separately. Broader
frameworks belong on the investigation list, but their inclusion does not approve
adopting their entire runtime, persistence or application architecture.

| Ecosystem | Candidates |
| --- | --- |
| Cross-ecosystem approach | Vendor SDKs where available; focused HTTP/streaming clients; official MCP SDKs where available |
| JavaScript / TypeScript | Vercel AI SDK; OpenAI Agents SDK; LangChain.js; LangGraph.js; Mastra |
| Go | Charm Fantasy; Google ADK for Go; vendor SDKs |
| Rust | Rig; `genai`; `async-openai` |
| .NET | Microsoft.Extensions.AI; Microsoft Agent Framework; Semantic Kernel |
| Python | PydanticAI; OpenAI Agents SDK; LangChain; LangGraph; Google ADK; LlamaIndex; Haystack; smolagents |
| Java / Kotlin | Spring AI; LangChain4j; Koog; Google ADK for Java |
| Elixir | LangChain for Elixir |
| Dart | `langchain_dart` |
| Ruby | RubyLLM; `langchainrb` |
| PHP | Prism; Neuron AI |

An empty ecosystem entry is not evidence that suitable libraries do not exist.
The inventory is a starting point, not an exhaustive package registry. Provider
subscription routes and authentication remain a separate qualification problem;
see [PROVIDER-RESEARCH.md](PROVIDER-RESEARCH.md).

**Existing exclusions remain:** Pi packages/runtime, Codex app-server and Copilot
CLI are not Urd foundations. Complete agent applications may remain architectural
references, not substitute foundations. Focused libraries and optional tool loops
remain eligible; the word "agent" or "framework" alone does not disqualify them.

## 3. Browser UI and full-stack approaches

Keep browser-client rendering distinct from backend agent execution. Some entries
also provide server features; they are not interchangeable with client libraries.

| Approach / ecosystem | Candidates |
| --- | --- |
| Browser primitives | Plain JavaScript/TypeScript; native Web Components; Lit |
| JS/TS component frameworks | React; Vue; Svelte; SolidJS; Angular; Preact |
| JS/TS application frameworks | Next.js; Nuxt; SvelteKit; SolidStart; Astro |
| Server-rendered HTML with selective interaction | HTML templates; htmx; Alpine.js; Go `templ`; Rails Hotwire; Laravel Livewire |
| .NET / F# | Blazor server-interactive; Blazor WebAssembly; Fable/Elmish; Bolero |
| Rust | Leptos; Dioxus; Yew |
| Dart | Flutter Web; Jaspr |
| JVM | Vaadin Flow; Compose Multiplatform web target |
| BEAM | Phoenix LiveView; Lustre for Gleam |
| Other browser-language approaches | Elm; ClojureScript with Reagent or re-frame |

Rendering mode, client/server connection dependence and sharing code across
surfaces are later evaluation subjects, not settled by this list.

## 4. Desktop clients, shells and native host integration

Browser delivery remains first. These entries concern the planned desktop client
and/or the background-app host, not a decision to add another agent backend.
Desktop-only choices do not independently satisfy the browser-client requirement.

| Role | Candidates |
| --- | --- |
| Web UI desktop shells | Electron; Tauri; Wails; Neutralinojs |
| Windows embedded web host | WebView2 with a minimal native/.NET host |
| Multi-platform UI toolkits | Flutter Desktop; Avalonia; .NET MAUI; Qt/QML; Compose Multiplatform; Dioxus Desktop; Slint |
| Windows-native UI/host options | WinUI 3; WPF; Windows Forms; direct Win32/tray integration |
| JVM desktop UI | JavaFX; Swing |
| VS Code client integration | Thin extension host plus webview; shared browser assets where appropriate |

Assess the desktop shell, backend and external tool processes separately before
combining their resource costs. Do not assume a shell/framework's language must
also own the model/tool loop.

## 5. Local builds, packaging and source distribution

These are orthogonal workflow options, not additional application frameworks.
Investigate both a prebuilt release and the user's ability to customise that
release from its supplied source.

- **Basic layouts:** editable source plus a pinned runtime; prebuilt executable
  plus matching source; embedded versus separately served UI assets; local
  bootstrap/build scripts using standard toolchains.
- **JS/TS:** bundled Node runtime; Node SEA; Bun ordinary standalone compilation;
  Bun experimental AOT as a distinct candidate; Deno compile. Package/build tools:
  npm, pnpm, Yarn, Bun tooling, Deno tooling, Vite, esbuild and Rolldown.
- **Go/Rust/Zig/C/C++:** native toolchains and their ordinary build/package paths;
  Cargo, Go modules, Zig build, CMake and Meson where applicable.
- **.NET:** framework-dependent deployment; self-contained JIT; single-file
  publishing; Native AOT; .NET SDK and NuGet for local builds.
- **Python:** uv-managed environments/runtime; conventional Python environments;
  PyInstaller; Nuitka; embedded-runtime distribution.
- **JVM:** Maven/Gradle builds; bundled runtime images with `jlink`; `jpackage`;
  GraalVM Native Image.
- **BEAM:** Mix releases; OTP releases; Rebar3; Gleam tooling.
- **Other ecosystems:** Dart/pub and native compilation; Swift Package Manager;
  Ruby/Bundler; PHP/Composer and the selected host's packaging route.
- **Windows delivery:** portable archive; MSI/WiX; MSIX; Inno Setup; NSIS.
  These are delivery alternatives, not a decision to create an automatic updater.

## Boundaries for the next stage

This list intentionally contains overlapping and potentially unsuitable options.
No numerical scoring, compatibility checks or comparative pros/cons have been
performed for the expanded inventory. The next stage can investigate candidates,
assemble coherent stacks and then score them against the agreed requirements.

Storage remains in [STORAGE-RESEARCH.md](STORAGE-RESEARCH.md), and provider access
in [PROVIDER-RESEARCH.md](PROVIDER-RESEARCH.md). This inventory does not reopen
settled product choices or add a requirement for a plugin platform, hosted build
service, automatic updater or multi-user server.
