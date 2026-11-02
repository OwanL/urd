# Urd direction: working notes

**Working document. Started 2026-11-02.** This captures the input and evidence
from the Pie "fork in the road" sessions (2026-11-01) so they survive once those
sessions are closed. It records what has been decided, what the maintainer said,
what the investigation found, and what is still open. It is not a spec. Update
it or replace it as decisions firm up.

Raw reports from the investigation are archived unedited in
[sources/2026-11-01-pie-direction/](sources/2026-11-01-pie-direction/). Line
counts and file paths in them refer to the Pie repository at commit `cc82abb0f`.

---

## 1. Decisions so far

| Decision | Detail |
|---|---|
| **Name** | The new project is called **Urd**. Pie stays as it is under its current name. |
| **New repository** | `OwanL/urd` (personal account), with `Owan-Lazic` (work account) as a collaborator. |
| **Public from day one** | A fresh repo with clean history also fixes Pie's personal-identity-in-history problem. Commits use the OwanL GitHub noreply address. |
| **Licence** | AGPL-3.0-only, the same as Pie, so Pie code can be carried across. |
| **Rebuild, not copy** | Pie's *behaviour* is the goal. Its code is a means. Re-architect for long-term maintainability rather than porting files one to one. |
| **Source-first customization** | Customization beyond plain config means editing source and rebuilding. No runtime add-on/plugin system and no UI plugin API. |
| **Add-on sharing is out of scope** | Installing and sharing add-ons is left to users. Urd doesn't provide a mechanism for it. |
| **Marketplace first** | The VS Code Marketplace (prebuilt) is the main way in. `git clone` is supported but secondary. Other channels come later. |
| **Pi upstream** | Not tracked. Pie already vendors Pi source and cherry-picks, and that has proved costly. Pi v0.80.6 mostly serves well, with known issues. |
| **Feature scope** | Essentially all current Pie features are core (see §3). |

## 2. Maintainer input (close to verbatim)

These are the maintainer's answers during the investigation. Keep them as the
reference for intent.

- **Opening position:** "Leaning toward the simplicity route… potentially strip
  out Pi as a dependency entirely, since it contains much functionality we do
  not need, i.e. the systems around customization. Packaging source with builds
  with a stripped down and simplified underlying harness sounds more appealing.
  Shipping runtime UI/guidance/extension/tool customization feels like far more
  complexity than it is worth." The maintainer asked for an unbiased
  investigation regardless.
- **Vision:** "An open source dev focused agent development environment /
  harness / UI that aims to support all the common workflows a developer would
  need, and in absence of this, devs can ask their agent to simply add whatever
  feature or integration they wish for."
- **Why a fresh repo:** "Much of Pie was discovery. Now we have something we
  know is good, it might be time to think about how things mature." Also:
  "Re-architect around the new approach of packaging source, dropping supported
  runtime customizability complexity, re-architecting things around the set in
  stone patterns. The codebase is large and complex and hard to reason about…
  too big for what Pie is."
- **Users who modify their install:** "Up to the user. Most users will make
  minor adjustments, probably not bothering to create their own repo, and just
  manage their local installation themselves. Some will create their own repo
  from what they have locally, or fork."
- **Entry point:** "Git will always be an option, but this is the less supported
  option. Marketplace will be the number one entry point starting off. We will
  later expand to other mediums."
- **Scope:** "Pretty much all current features are core. The UI is quite
  refined as is. The agent tooling, dynamic tool discovery, token pruning,
  session controls, subagents etc. all work very well." If rebuilt, "more time
  would be put into subagents to allow things such as interruption recovery,
  i.e. a session is stopped but the subagent can continue, or the session is
  then resumed."
- **Pi upstream:** "I do not care. We have already decided to package Pi source
  and pick and choose new features as they come. However, the overhead of doing
  so is a lot."
- **Add-ons:** "Installing and sharing add-ons is left to the users themselves.
  They can figure this out."
- **On the outcome:** "Of course we wouldn't copy things one to one if we
  started from a new repo. We would consider the architecture, treating the
  behaviour of Pie as the end goal, but the code as a means to get there,
  optimizing the latter for the former's long-term maintainability."

### Earlier context (2026-11-01 morning session)

This led into the direction question:

- Pie could already build a Windows VSIX (318 MB with bundled Node, Chromium
  and web/MCP integrations), but wasn't release-ready. Still missing:
  fresh-user setup without the maintainer's checkout, the full
  install → configure → task → restart → update journey, provider onboarding,
  and licence/notice/naming/history-privacy clearance.
- Builds: the maintainer wanted "one build for all". After review, the
  refinement was **one shipping runtime, built once and installed as the same
  bytes, plus a separate fast development loop**. There would be no separate
  developer edition.
- Customization: the maintainer first explored UI extensibility, where tools
  bring their own UI and users can replace UX flows. They then proposed
  **packaging source with every build** so users get full control and Pie can
  drop its extension/UI/module customization machinery. That proposal is what
  this investigation tested.

## 3. What Urd must do (behaviour scope)

This is the feature list as of Pie today. It is not yet a spec.

- Refined chat/transcript UI (Preact) in a VS Code sidebar, plus a standalone
  and browser host.
- Multiple sessions, session controls, and cross-session coordination.
- Subagents. Investment target: **subagents survive parent interruption and
  continue when the parent session resumes**.
- Provider and model management (GitHub Copilot, OpenAI/Codex, Ollama and others),
  including auth.
- Dynamic tool and skill discovery (`request_capability` / `invoke_tool`).
- Token management: tool-result pruning and compaction.
- MCP and web search/fetch.
- Browser automation (Playwright) and computer use.
- Analytics and cost tracking.
- Durable transcripts and crash recovery: never lose a transcript and never run
  an operation twice.
- Plain-file config for instructions/AGENTS.md, named agents, skills, MCP and
  providers.

## 4. How the investigation was run

Six independent reports across three providers and six models. One report
deliberately argued the maintainer's side, and the coordinator spot-checked the
main code claims.

| Report | Model | Role |
|---|---|---|
| [harness-survey](sources/2026-11-01-pie-direction/report-harness-survey.md) | GPT-6.1 (OpenAI) | How other coding agents handle customization and distribution |
| [malleable-precedents](sources/2026-11-01-pie-direction/report-malleable-precedents.md) | Claude Opus 5.5 (Copilot) | Source-first vs runtime-extensible precedents outside coding agents |
| [complexity-audit](sources/2026-11-01-pie-direction/report-complexity-audit.md) | Kimi K2.7 (Ollama) | Pie's dependence on Pi and where its size lives |
| [redteam](sources/2026-11-01-pie-direction/report-redteam.md) | Grok 4.7 (Copilot) | Strongest case for source-first plus a fresh repo |
| [complexity-deep-dive](sources/2026-11-01-pie-direction/report-complexity-deep-dive.md) | Gemini 4 (Copilot) | Skeptical second opinion on size, based on reading code rather than counting lines. **The most useful report.** |
| [decision-judge](sources/2026-11-01-pie-direction/report-decision-judge.md) | DeepSeek V4 Pro (Ollama) | Independent verdict that challenged the others |

The shared brief given to all investigators is
[BRIEF.md](sources/2026-11-01-pie-direction/BRIEF.md).

## 5. Findings

### 5.1 Customization model

- **Keep as plain files** (cheap; every other harness has them):
  instructions/AGENTS.md, named agents, skills, MCP config and provider config.
  Keep defaults and user overrides in **separate layers**. Never copy defaults
  into user-owned files; Omarchy shows that breaks updates.
- **Drop:** the runtime extension loader, package installer, settings-layering
  framework, terminal UI, and any UI plugin API. GUI plugin APIs take years to
  change once people depend on them (Firefox XUL, Grafana, Backstage, OpenCode
  v1→v2).
- **Everything else is a source edit.** The Marketplace install stays prebuilt.
  A "customize" flow should fetch the exact matching source into a user-owned
  git repo, build it, and make the installed extension run the user's build.
  Never edit installed files in place.
- **Source-first only works if the code is small.** Every precedent where it
  works has a tiny core or tiny units: dwm (~2k lines), kickstart.nvim (one
  file), shadcn/ui (leaf components). Pie plus its Pi fork is ~421k non-test
  lines, so a user maintaining that would hit the Cursor/VSCodium fork problem
  (lag and papercuts). **This choice depends on Urd being much smaller.**
- **Precedents that last still add seams.** NanoClaw (agent-maintained fork)
  ended up with additive modules, contract tests at each touch point and an
  update tool that rebases in a worktree with rollback. Two options came out of
  this: keep core seams small and named (e.g. a tool registry, a renderer
  registry where adding a file plus one registration line is enough), and
  optionally offer an agent-driven update/rebase flow later.
- **Disagreement:** the decision judge wanted to **keep Pi's runtime
  tool/extension seam** for third-party packages (pi-mcp-adapter,
  pi-web-access). The red team said those are pinned, patched dependencies
  behind a guard, not a free ecosystem, and should be absorbed. The
  maintainer's "add-on sharing is left to users" answer supports absorbing them
  as ordinary source.
- AGPL already requires publishing the matching source, so making source
  available is a legal requirement before it is a design choice.

### 5.2 Pi

- Pie's Pi fork is based on **v0.80.6** (2026-07-09), imported 2026-10-01 at
  `44caef3a1`. Upstream has shipped 32 releases since then (1.0.0 → 1.4.0 by
  2026-10-21) and none were merged. Pie's changes to the fork total
  98 files, +18,437/−737, mostly in `agent-session.ts` (+1,563) and
  `session-manager.ts` (+1,372). In practice Pie already owns Pi.
- **Recommendation: absorb, don't drop.** Keep `pi-ai` (provider layer,
  ~39k lines) and the core session runtime from `pi-coding-agent`
  (`AgentSession`, `SessionManager`, compaction, auth, model registry,
  ~16.8k directly used). That is roughly **45k–55k lines** to treat as Urd's own
  code. Rewriting it would cost months for little gain.
- **Delete:** `pi-tui` (~12k lines, imported by one file), interactive/CLI mode
  (~16.6k), the extension loader/runner (~4.4k), the package manager (~2.7k), the
  settings manager (~1.2k) and the resource loader (~1.1–1.9k). That is about
  23k–38k lines.
- `pi-agent-core` isn't the live loop. The real loop is inside
  `pi-coding-agent`'s `agent-session.ts`.
- Keeping this code is a **permanent ~50k-line commitment**. It is the least-bad
  option, but it is a liability Urd takes on, not just a deletion.

### 5.3 Size: the maintainer was right

The first audit called most of Pie "essential". The deep-dive, which read the
code, showed that "not dead" was being confused with "essential". The
coordinator spot-checked its claims and they held up.

- Pie's own code is ~285k non-test lines, ~300k test lines, and ~136k for the Pi
  fork. That is ~421k non-test lines in total. The "570k" figure includes tests.
- **It is 2.5–3× larger than its features need.** Roo Code is the closest peer
  (VS Code GUI, subagents, MCP, Playwright, 40+ providers, cost tracking) and is
  ~197k lines including its own provider layer. Aider is ~66k.
- **Lean target:** ~95k–120k lines of Urd's own code plus ~45k–55k of trimmed Pi,
  so **~145k–170k total**.
- **The bloat comes from the architecture, not dead code.** The top drivers, with
  rough lines involved:
  1. **Four process hops** (webview ↔ host ↔ coordinator ↔ worker), with each
     layer tracking the same session state. The host reducer/effect machine
     (`conversation-state/`) alone is 24k lines. ~45–55k. *Merge host and
     coordinator into one backend.*
  2. **Pi customization indirection**: Pi loaders, `extensions/*` shims, the
     managed-package patcher and `sdk.ts` artifact loading. ~28–32k. *Register
     tools directly.*
  3. **Distributed-systems-grade safeguards for a local app**: six operation
     ledgers that prevent duplicated work, three session-ownership authorities,
     file-hash leases, a Proxy fence, and a four-stage render commit proof.
     ~25–30k. *Use one lock and plain state push.*
  4. **Analytics**: 32.6k lines, 26 tables, 14 migrations, and a `legacy/`
     tracker still live. ~22–25k. *Replace with a ~5-table SQLite recorder and
     one query service (~6k).*
  5. **VSIX bundling**: a 318 MB package (Playwright ~307 MB), hand-written
     ZIP/TAR/PAX parsers, and immutable generations. ~14–18k. *Download Chromium
     on first use or use system Chrome/Edge.*
  6. **A second paging/JSON-patch system for subagent details.** ~8–10k. *Reuse
     the main transcript pagination.*
  7. **Custom build/test tooling**: a test runner, package-resolution
     workarounds and import-graph checks. ~8–10k. *Use standard workspace
     resolution plus Vitest.*
- Evidence spot-checked: `harness/tools/subagent/runner.ts:1288` matches process
  listeners via `fn.toString()`; `scripts/build/playwright-runtime-payload.mjs`
  hand-parses TAR/PAX; `analytics/legacy/stats-service` is still wired into the
  backend.

### 5.4 Process is what caused the growth (the biggest risk for Urd)

- **270 test files read source code as text.**
  `test/integration/sync-contract.test.ts` regex-matches `STATE_CONTRACT.md`
  (146 KB) and reducer source 181 times. Other tests regex-match CSS values.
  As a result, simplifying any internal design breaks tests. That is why
  cleaning up Pie in place kept failing.
- About **25% of `STATE_CONTRACT.md` describes behaviour users rely on**, for
  example: model selection sticks to the session, closing a private session
  scrubs its data, and empty submit only continues when continuing is possible.
  The other ~75% pins internal field names and protocols.
- Roughly 50–60% of Pie's tests check real behaviour and encode valuable domain
  knowledge. Those are worth mining.
- **Without new rules, Urd will grow back to 500k.** Changing the remote
  doesn't change how agents write code. Proposed rules from day one:
  - Tests check behaviour, never source text, CSS or doc prose.
  - Contracts list only what users rely on. No pinned internal names.
  - Each area has a size budget.
  - Every safeguard (lease, fence, ledger, retry) must point to a real bug it
    prevents.
  - Use standard tooling (workspace resolution, Vitest, esbuild/Vite) before
    writing custom tooling.

### 5.5 Rewrite vs evolve

- Phase-1 reports (survey, precedents, audit) said **evolve Pie in place** using
  the strangler pattern. They assumed the complexity was necessary. The
  deep-dive showed it isn't, and that changed the recommendation.
- The red team's caveat still applies: a fresh repo that ports today's code is
  "a copy with a new remote". The maintainer's "behaviour is the goal, code is
  the means" framing is what makes this a real redesign instead.
- Spolsky's warning still applies to the **knowledge**, not the code. Pie's
  ~1,450 commits are mostly durability, recovery, Windows and lifecycle fixes.
  Writing code is cheap; rediscovering edge cases is not. That knowledge has to
  be written down (see §7).
- **Don't rewrite well-contained parts for the sake of it.** Copy the internals
  of individual tools, UI components, provider handling and the trimmed Pi core,
  and redesign how they plug in.
- **Keep one live codebase.** Pie gets bug fixes only, and Urd should be used
  to build itself as early as possible. Two active architectures will sink one
  maintainer.
- **Rough cost:** 2–4 months to match Pie's features. This is a guess.

## 6. Delivery notes

- One shipping runtime per platform: build once, test, and install those same
  bytes. Keep a separate fast development loop, but don't treat it as release
  acceptance.
- Source-first is a trap unless building a matching tag on a clean Windows
  machine is **one boring command**. Pie's build relies on Proto, pnpm, Moon,
  bundled payloads and immutable generations, which is the opposite of that.
- VSIX size comes from Playwright/Chromium, not Pi.
- Windows comes first. macOS and Linux need real portability work (locking,
  storage, native payloads).
- Release order planned in Pie: VS Code extension → Electron desktop app →
  website.

## 7. Open questions and next steps

**Proposed next steps from the session.** The maintainer cancelled the
confirmation question, so these are not yet agreed:

1. **Behaviour spec.** Pull the user-visible behaviour out of
   `STATE_CONTRACT.md`, the behavioural tests and fix-heavy commit history into
   a short document organised by feature. Include hard-won edge cases: crash
   recovery, transcript durability, no duplicate operations and Windows quirks.
   It becomes Urd's spec and behaviour-test list.
2. **Core prototype.** One backend, one worker on the trimmed Pi core, and the
   existing UI, able to send, stream, save, restart and resume a session.
   Measure the time it takes and the lines it needs. *Go/no-go:* if it comes in
   small and fast, continue with Urd; if not, reconsider trimming Pie in place.
3. **Agent rules for Urd** (§5.4) written into Urd's `AGENTS.md` before any real
   code lands.

**Open questions:**

- Process topology: is a separate worker process per session still wanted
  (isolation, crash containment), or can sessions run in the backend process?
- Is an internal source-level renderer seam (one file plus a registration line)
  wanted from the start?
- Does analytics stay a core product feature at full depth, or is a lean
  cost/usage recorder enough?
- Bundle Chromium in the VSIX, or download it on first use / use the system
  browser?
- How is a user's own build activated from the Marketplace install (the
  "customize" flow)? When is it needed: v1 or later?
- Should MCP and web search be absorbed as ordinary source, and if so, rewritten
  or vendored?
- Subagent interruption recovery: what are the exact semantics when the parent
  is stopped, closed, or the host restarts?
- Which hard-won Pie behaviours are worth porting as-is, and which were only
  needed because of Pie's architecture?

## 8. Things not to lose from Pie

- Pie repo: `C:\dev\repos\pie` (private, history contains personal identities;
  not to be published as-is).
- Key Pie docs to mine: `docs/contracts/STATE_CONTRACT.md` (behavioural ~25%),
  `docs/architecture/ARCHITECTURE.md`, `docs/architecture/UI-DESIGN-PHILOSOPHY.md`,
  `docs/plans/PIE-PRODUCTIZATION.md`, `docs/plans/RELEASE-JOURNEY-ACCEPTANCE.md`,
  `docs/contracts/*` (agent session control, subagent provider resilience, tool
  result pruning, session titles, deferred triggers).
- Source sessions (in Pie's `data/outcomes/sessions/`):
  `2026-11-01T08-25-26-994Z_01a19636…` (productization and customization
  discussion) and `2026-11-01T20-31-15-820Z_01a198cf…` (this investigation).
