# Red team: source-first, and a fresh repo

The hybrid (keep Pi, evolve in place, source edits not primary) is the conservative reading of a codebase that works. It is the wrong shape to publish. Ship a Marketplace VSIX whose matching source is the customization surface, in a new repository whose agent core is the slice Pie already owns. Do this before the first public listing.

## Source edits are already the real API

Pie's philosophy is that a missing feature is a request to the agent. Productization (2026-10-27) then built a second product: packaged defaults, user/project layering, and executable Pi extensions, with `customize-pie` pointing agents at the installed SDK. That second product cannot reach what users will want changed. Pi's `ExtensionUIContext` is a terminal contract (`setStatus`, `setWidget`, `onTerminalInput`, themes). Pie's host bridge stubs those methods; the subagent UI proxy no-ops them because there is no terminal. A UI-extension system would freeze the Preact renderer the same way. The plan already refuses arbitrary Pi terminal extensions. What remains is a narrow hook surface that still forces internals to stay compatible with strangers' code.

The shims show the mismatch. `extensions/` is 13 files, 749 lines. `extensions/subagent/index.ts` is a six-line re-export so Pi auto-discovery and a stable extension ID survive, while the implementation lives in `harness/tools`. Outside the fork, 102 files import Pi; the dominant symbols are `ExtensionAPI`, `ToolInfo`, `Skill`, `AgentSession`. Pie grew an explicit tool catalog and kept a plugin loader so the catalog can be discovered.

Data config should stay: instructions, named agents, skills, MCP definitions, provider YAML. Stock users install the VSIX and never see source. Customizers clone the matching tag, edit, and install their own generation. Installed files stay immutable. AGPL-3.0-only makes matching source a publication gate, not a taste. An extension API is not corresponding source. The day `customize-pie` ships to strangers, loader order, resource precedence, and `ExtensionAPI` become compatibility. There is no Marketplace listing yet. That promise is free to not make.

## The fork is already owned, and most of it is not the product

`44caef3a1` (2026-10-01) imported pristine Pi v0.80.6. Upstream shipped 1.0.0 the same day, then 32 releases through 1.4.0. Pie's later commits never resynced. The delta is 98 files, +18,437/−737, almost all in `coding-agent`: `agent-session.ts` +1,563, `session-manager.ts` +1,372, plus model-context periods and durable records. Production `pi-ai` moved by about 42 lines. Production `pi-tui` did not change. A rebase onto 1.0 is not available: 1.0 deleted the experimental harness in `pi-agent-core`. One maintainer already failed the rebase test in a month. Calling the dependency "Pi" hides an owned fork and blocks deletion.

Hand-written fork source is about 100k lines, plus 16k of examples and 95k of fork tests. Interactive mode is 16.6k, tui 12.2k, extension loader/runner/types 4.4k, package manager 2.7k, settings manager 1.2k, resource loader 1.1k. Roughly 38k lines are a terminal application and a plugin host. Pie's UI does not use them. What Pie runs is the session object, the transcript manager, the provider HTTP layer, and a tool registry. `sdk.ts` is already a 1,013-line "minimal SDK contract" wrapped in fingerprints and dual package names. Port the immutable-install rules. Do not ship the TUI to justify them. The 2026-10-27 VSIX was 331 MB, of which Playwright was about 307 MB; deleting `pi-tui` shrinks the surface one person must hold, not the download.

## A fresh repository is the cheap moment, and it is a port

The checkout is six months old (2026-05-06, 1,443 commits, one maintainer). Non-test code outside the fork is on the order of 280k lines, tests roughly as large, and the README still describes a personal `pi-config` stack. Productization says git history contains author identities and that no history rewrite is authorized. A new public repo is the privacy decision that does not rewrite the private archive.

Spolsky's Netscape essay is the right objection and the wrong analogy. Netscape had a market and threw away bugfixes for years. Pie has no public users to lose. The encoded knowledge is the state contract, write leases, recovery fencing, and the session-manager delta. A blank-slate agent loop drops that. Copy those modules and their contract tests, then delete the rest.

AI changes the cost of a specified port, not of invention. Agents can carry `agent-session.ts` across a repo boundary; they cannot reinvent interruption fencing, and in-place deletion got cheaper too. The fresh repo wins on a public history and on cutting lines the GUI never calls. It loses if it becomes a second living product. Freeze the old checkout until the port passes the same recovery and session contracts.

## What the new repo is

Marketplace VSIX stays the entry point. Git clone is how you customize. Data files only for instructions, agents, skills, MCP, providers. No extension loader, package manager, resource-precedence framework, or `ExtensionUIContext`. Tools register in the owned catalog. Absorb or replace `pi-mcp-adapter` and `pi-web-access`. Keep `pi-ai` as a thin vendor and treat catalog updates as cherry-picks. Cut `coding-agent` to session, transcript, durable records, model-context policy, and a direct tool registry. Port the state loop, session-storage, analytics, tool implementations, and the generation rules that stop a custom build from patching the installed tree.

Do not launch the extension contract and then delete it, and do not re-architect the reducer because the repo is new. Source-first is a trap if `clone && build` stays a Proto/pnpm/Moon research project. Two active architectures will sink one maintainer.

## After phase-1: what survives

The three reports do not rescue "keep the Pi extension host." They do kill the stronger packaging of this case. Read them as a correction, not as a reason to drop the deletion list.

**Survives.** The fork is owned: the complexity audit finds merges impractical, `pi-tui` dead, and `pi-agent-core` not the live loop. Do not build a GUI plugin API; Firefox XUL, Grafana, Backstage, and Amp's client-specific hooks are the same tax. Publishing `customize-pie`'s wide SDK contract before any external user exists is still avoidable. Privacy is unrefuted, and corresponding source still wants a matching tree per build. `pi-web-access@0.27.0` and `pi-mcp-adapter@2.20.1` are pins behind a guard, not a free ecosystem. VSIX bulk is Playwright, not the fork.

**Does not survive.** "Source edits are the philosophy, so make them primary" over-reads Pi. Upstream's answer is extensions, and an agent that can write a patch still cannot erase rebase, test, toolchain, and distribution cost. Precedents where source-first works (dwm, shadcn, kickstart) edit a tiny unit. Pie plus the fork is ~400k non-test lines. NanoClaw, the closest agent-maintained case, rebuilt seams and an update tool because core edits are what the model cannot protect. Config-to-400k-lines is a cliff, not a slope. The 749-line shim count understates the cut: about 29k lines of tools are extension-shaped, and the audit prices re-plumbing them at 3–5k lines of glue if the session runtime stays. That is an in-place job, not proof of a new repo. "AI changes rewrite economics" does not survive. Writing is cheap; the durability, recovery, and Windows commits are not. A fresh repo that ports the current feature set is a copy with a new remote. Privacy and architecture were conflated here: a squashed public mirror solves identities without a second architecture.

**When this path is actually the right call.** All of these have to be true, and they are checkable:

1. **Scope is cut on paper first.** If the port list is still ~55k frontend + ~46k backend + ~47k agent-processes + ~27k analytics + ~27k tools + ~14k session-storage, do not open a new repo. A fresh repo is justified only if named subsystems are left behind and the intended non-test core is small enough to hold — well under the current ~280k, not a reshuffle of it.
2. **An in-place spike fails.** Spend a bounded effort on the audit's Scenario A: drop TUI from the runtime artifact, register one tool family without `extensions/`, keep `pi-ai` and the session runtime. If shim and resource-layering tests make that longer than a port, the fresh repo earns its keep. If the spike lands, evolve in place.
3. **The old tree freezes.** No feature work on both. The new tree must pass the existing recovery and session contract tests, not a new suite that defines success as "compiles."
4. **A user-fork rehearsal works before the promise.** An agent rebases one intentional source patch across one Pie release, in a worktree, with rollback. Separately, time a rebase of `harness/pi` onto Pi 1.4.0. If that rebase is painful for this maintainer, do not tell users their agent will maintain a Pie fork. Source-first stays the top of the slope only, and only after the build of a matching tag is one boring command on a clean Windows machine.
5. **The extension contract has not shipped.** After strangers depend on `ExtensionAPI`, this path waits on a deprecation window. Before that, the right move that does not need a new repo is: do not ship the wide API. Ship data config, matching source, and a direct tool registry.

Absent those conditions, the honest call is the audit's narrow one: keep `pi-ai` and the session runtime, delete the terminal and the plugin host, wire tools directly, and treat a new repository as a publication mirror rather than a second architecture.
