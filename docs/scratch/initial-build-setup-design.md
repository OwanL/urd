# Initial build/setup design — owner review proposal
2026-11-05. **The full command/configuration workflow remains proposed, not approved.**
Minimal Core/Server/test metadata is implemented for the approved Core and scoped hosting
slices; see [backend development](../backend-development.md) for actual pins and verified commands.
The wider Windows-first checkpoint below (Core + Server + built Preact UI and a
deterministic request-only model) remains proposed. A runnable [local HTTP Server](../server-development.md)
now exists, using ordinary .NET commands; UI and bootstrap/build wrapper scripts remain
unimplemented. The current baseline keeps its few package pins in the projects.

## Existing direction and scope
- [Application plan](../APPLICATION-PLAN.md): component-led source; `scripts/`
  owns executable automation. Core is ASP.NET-free; Server references Core.
- [Foundations](../REPO-FOUNDATIONS.md): .NET 10 LTS, Vite, preferred npm;
  copy-local tool acquisition where practical; each customized copy keeps its data.
- Exact supported pins follow support lifetime/dependency compatibility at implementation,
  not installed versions. No owner vote on ordinary compiler/test-runner selection.
- No release/package/source-capture system, LAN, installed activation, credential work,
  shared tool-cache manager, empty future modules or second fixture application.

## 1. Minimum authored inputs and configuration ownership
```text
<copy-root>/                       checkout or writable customized copy, not cwd
  scripts/urd.ps1                   one public command entrypoint
  source/Urd.slnx                   Core, Server and one backend test project
  source/global.json               exact supported .NET 10 SDK; no roll-forward
  source/Directory.Build.props     nullable/compiler/analyzer defaults for C# + tests
  source/Directory.Packages.props  central backend/test dependency versions
  source/.node-version             exact supported Node; use its bundled npm
  source/backend/Urd.Core.csproj   normal backend subtree; no ASP.NET dependency
  source/hosting/server/Urd.Server.csproj   Web SDK; reference Core
  source/ui/package.json           Preact/Vite/TS, check/build/smoke npm scripts
  source/ui/package-lock.json      committed locked npm dependency graph
  source/ui/tsconfig.json          strict; check uses tsc --noEmit
  source/ui/vite.config.ts         UI-only Vite inputs/output configuration
  source/tests/Urd.Backend.Tests/Urd.Backend.Tests.csproj
  .tools/                         acquired SDK/Node; regenerable, not cleaned
  builds/                         only generated build/test output
  data/                           copy-owned persistent data, never an input/output
```
NuGet `packages.lock.json` files live beside the three C# projects; projects enable
lock generation, normal commands restore in locked mode. Pins/locks change only by
an explicit maintenance edit, never Setup. No additional task runner/version manager.
Solution/project/config cwd is `source/`; npm cwd is `source/ui/`. No parent/home
config is copied into the recipe. Non-secret required build config travels with source.
Tests reference Core and Server as needed; they reuse actual routes/storage/model paths.
No production behavior is copied into a separate smoke app or sample server.

## 2. `scripts/urd.ps1` — command lifetime, not a background manager
Declarations below are review shapes, not executable implementations:
```powershell
param(
  [Parameter(Mandatory)][ValidateSet('Setup','Check','Build','Run','Clean','Smoke')]
  [string] $Command,
  [string] $CopyRoot = (Split-Path $PSScriptRoot -Parent),
  [ValidateSet('Debug','Release')][string] $Configuration = 'Debug',
  [switch] $InstallBrowser # valid only for Smoke, explicit browser acquisition
)
function Resolve-CopyLayout([string] $CopyRoot) { } # absolute owned path record
function Ensure-Toolchain($Layout) { } # explicit SDK, Node and npm executable paths
function Restore-Dependencies($Layout, $Tools) { } # locked restore, never upgrade
function Invoke-Child([string] $File, [string[]] $Arguments,
                      [string] $WorkingDirectory, [hashtable] $Environment) { }
function Build-Urd($Layout, $Tools, [string] $Configuration) { } # output paths
function Start-Urd($Layout, $Tools, $BuildOutput) { } # foreground child; await exit
function Remove-BuildOutputs($Layout) { } # fixed generated targets only
function Test-UrdSmoke($Layout, $Tools, $BuildOutput, [switch] $InstallBrowser) { }
```
Layout owns resolved copy/source/tools/build/data paths for this invocation; no global
mutable configuration. Child invocation checks exit codes, quotes argument boundaries,
sets explicit cwd/environment and propagates failures. No shell-composed command strings.
One command per copy holds a small exclusive command-lock file in `.tools/` through
child completion; Run keeps it until host exit, including cancellation/cleanup. Competing
commands fail clearly, not with stale PID kills. No detached host/automatic browser opening.
Direct Server startup still needs the hosting owner's separate database lifetime lock.

## 3. Tool/restore boundary and command effects
- `Ensure-Toolchain`: use validated declared versions; when missing, acquire official
  host-architecture SDK/Node payloads into `.tools/`, verify downloads/completed versions,
  then invoke explicit executables. Unsupported hosts/policy/network failures stop clearly.
  Never install globally, mutate persistent PATH/profiles or substitute ambient versions.
  Child-only PATH and DOTNET_ROOT ensure npm/MSBuild subprocesses use these same tools.
- `Restore-Dependencies`: `dotnet restore Urd.slnx --locked-mode` with source cwd;
  npm uses `npm ci`, not `npm install`. Standard NuGet/npm package caches may be shared;
  no new cache manager. A small successful npm-input fingerprint beside node_modules
  avoids reinstalling unchanged dependencies; missing/changed inputs trigger `npm ci`.
  Setup forces dependency repair; failed/interrupted restore never records success.
- **Setup:** ensure tools + locked restores; no build, data creation or browser download.
- **Check:** ensure tools/restores, compile C#, strict TS check, fast behavior tests using
  deterministic model + real isolated SQLite. No browser acquisition/launch or provider.
- **Build:** ensure tools/restores, compile Core/Server into `builds/<Configuration>/app/`,
  then Vite emits built UI into that app's `wwwroot/`: one coherent runnable output.
  C# intermediates remain normal bin/obj; UI dependencies remain node_modules. No archive.
- **Run:** Build, then execute that Server with that built UI and explicit copy data;
  does not run a Vite development server or use Node as an application server.
- **Clean:** needs no SDK/Node/network; remove declared builds, C# bin/obj and UI
  generated output only. Keep data, tools, dependency caches and node_modules.
- **Smoke:** separate built-UI/Kestrel/SQLite test, using the actual production host.
  Requires a pre-acquired compatible Playwright Chromium or explicit `-InstallBrowser`.
  Browser acquisition never sneaks into Setup/Check/Build/Run.

## 4. Proposed command wrapper — host startup flags now approved
```powershell
.\scripts\urd.ps1 Setup
.\scripts\urd.ps1 Check
.\scripts\urd.ps1 Run -CopyRoot 'C:\dev\repos\urd'
.\scripts\urd.ps1 Smoke -InstallBrowser # one explicit browser-acquisition route
.\scripts\urd.ps1 Clean
# Start-Urd invokes selected dotnet + <copy>\builds\Debug\app\Urd.Server.dll:
# --data-root <absolute copy>\data --port 0
```
The approved local host uses `--data-root <absolute path> --port <0..65535>` and
reports `urd-ready` as documented in [server development](../server-development.md).
The commands above, `CopyRoot` script behavior and built UI layout remain proposals.
`CopyRoot` belongs to that future script, which supplies absolute DataRoot.
Host owns option parsing, loopback/browser checks, exclusive data-directory/database
ownership and initialization before readiness. Static assets resolve from
`Path.Combine(AppContext.BaseDirectory, "wwwroot")`, never cwd/source/data; no UI-root flag.
UI uses the same served origin, never a guessed port/client-selected database. Initial
composition uses only the deterministic model; no provider selection/credentials API.
Host reports one structured `ready` stdout line with the actual loopback URL after
storage initialization/listen succeeds. Wrapper displays it; Smoke parses it with a
bounded timeout, never infers readiness from process creation or a fixed sleep.
Smoke supplies fresh owned OS-temp data to this same runnable output; awaits
its own host exit before deleting only its own fixture. Failure preserves diagnostics
outside the repo; timeout/failure must not kill unrelated hosts or delete live data.
Normal Run keeps its data after host exit; restart reopens it, not a fresh dataset.

## Safety, concrete acceptance and two trade-offs
- Resolve canonical paths before effects; reject output/source/data overlap and linked
  deletion targets/ancestors. Clean uses fixed exact targets, no user-supplied delete glob;
  data is not traversed, emptied or reset. An active same-copy Run blocks Clean/Build.
- After approval: A creates chat → rebuild/clean → A reopens chat; independent copy B
  starts empty. SQLite fast tests use unique temp data, never `data/` or personal sessions.
- Smoke loads the built UI, submits deterministic chat, sees persisted output and reopens
  the same temporary SQLite data after restart. Also check process failure/readiness errors,
  copy paths containing spaces, clean preservation and missing-tool acquisition boundaries.
- **Trade-off 1:** per-copy tools duplicate disk/downloads, but keep ownership/version
  selection simple; do not introduce shared tool population/cleanup coordination yet.
- **Trade-off 2:** foreground Run plus one command lock limits same-copy build/watch while
  running, but avoids overwriting/deleting active outputs or introducing a host supervisor.
- Integration request: hosting confirms data-root/port flags, AppContext `wwwroot`,
  readiness/database lock/lifecycle; UI confirms Vite output/Playwright runner.
  Exact private helper names remain revisable.
