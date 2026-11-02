# Decision judge: Pie direction

Independent assessment. The three phase-1 reports are unusually convergent: all three land on **hybrid customization (D)** and **evolve in place**. That agreement is partly genuine (the evidence genuinely points there) and partly a shared "don't rewrite" prior. I form my own view below, then note where I think they are wrong or one-sided.

The single most important framing correction: **the two questions are entangled through one fact the reports acknowledge but do not integrate — Pie's git history contains personal identities that must be resolved before any public repo.** That privacy fact is a *forcing function* on Q2 that makes "fresh public repo seeded from the current tree, history reset without rewrite" not a rewrite at all but a near-free, mandatory first step. It changes the Q2 answer from "evolve in place in the current repo" to "seed a clean public repo, then strangler-evolve inside it."

---

## Q1 — Customization model

**Recommendation: D (hybrid), with a sharper split than the reports draw, and no GUI plugin API.**

The slope, cheapest-to-costliest, should be:

1. **Data/config (stay runtime, no build):** instructions/AGENTS.md, named agents, skills, MCP config, provider config. These are portable workflow assets; keep them as *layered overrides*, never as copied defaults (Omarchy's lesson). This is ~the current settings/resource surface minus the extension loader.
2. **A narrow executable-tool boundary (stay runtime):** keep Pi's tool/extension seam — it already delivers `pi-mcp-adapter`, `pi-web-access`, and a third-party ecosystem at zero cost. Do **not** strip this. This is where the reports and I agree, and where I disagree with the most aggressive reading of "source-first."
3. **One narrow source-level renderer seam (new, cheap):** a `renderers/` registry where a tool can register a Preact renderer by adding a file plus one registration line. This is **not** a runtime plugin API and not versioned as a public contract yet — it's an internal seam that the user's agent writes into their source fork. I diverge from the reports here (see challenges).
4. **Source-first (the top step):** anything else — arbitrary UI, core UX, runtime behavior — is "edit the source and build." Delivered via a **"customize Pie" flow**, not by editing installed files.

**Source-first delivery (Marketplace is entry #1):** the VSIX remains the prebuilt, checkout-free default. Every release also publishes (or makes one command obtain) the *exact matching source*. "Customize Pie" should: fetch that source into a **user-owned git repo** (a fork, not a patch against installed files), build it with a one-command Windows build (this does not exist today — the 318 MB immutable-generation VSIX is the opposite of a modify/rebuild loop), and make the installed extension load the **user's build** instead of the bundled runtime. Updates = `pie update` style flow that rebases the user's fork in a scratch worktree, runs seam contract tests, snapshots state, and rolls back on failure — never a raw `git pull` (the NanoClaw model). Users who make no source edits keep getting normal Marketplace updates for free; only source-edit users pay the rebase cost, and they self-select.

**Three strongest reasons:**
1. **The config/tool surface is where cross-tool sharing already happens and it is cheap to keep.** Instructions, skills, MCP and providers are what users actually customize for workflow reasons; dropping them to force source-first would make the common case a 400k-line fork.
2. **The Pi tool seam buys an outside ecosystem at no maintenance cost.** Dropping it means vendoring or rewriting `pi-mcp-adapter`/`pi-web-access` (~1.4k lines of patch logic) and killing a real distribution channel. There is no upside.
3. **A GUI plugin API is the most expensive surface to keep stable, and there is no demonstrated demand.** Grafana/Firefox/OpenCode show it costs years; Pie's 6-month-old UI is still churning. Source-first plus one internal renderer seam gives ~90% of the value at a fraction of the commitment.

**Biggest risk:** the "strip Pi's extension/resource layer" work (which D entails for the *loader* machinery) is under-estimated. The complexity audit says "3–5k lines of glue to re-plumb ~29k lines of extension-shaped tools." **Mitigation:** treat that number as a floor, not an estimate — the `tool-and-skill-selection` subsystem (~4.4k src / 9.5k tests) and `agent-instructions` are *bridge* code that also shifts, and Pi's skill/AGENTS.md discovery semantics have already diverged. Prototype the re-plumb on one tool before committing (experiment below).

**Effort (single maintainer + AI agents):** keep-config/keep-tool-seam = no work; renderer seam = ~1–2 weeks; "customize Pie" source flow = ~2–4 weeks; strip extension/resource/package/TUI loader layers + re-plumb = **6–12 weeks**, dominated by the re-plumb and test migration, not by line deletion.

**Cheap experiment (days):** (a) re-plumb **one** tool (`ask-user` or `warm-bash`) directly against the core `pi-coding-agent` runtime, bypassing the extension loader, and measure the real glue vs. the 3–5k estimate. (b) Have an agent rebase `harness/pi` onto Pi 1.4.0 and time the breakage — this calibrates whether "own the fork" stays cheap.

**Flip questions:** (1) Does the preview cohort show *demonstrated* demand for arbitrary UI change *without* rebuild within ~6 months? If yes, promote the renderer seam to a versioned runtime contract sooner. (2) Is there real demand to install **third-party** tools outside Pie's own tree? If strong, *expand* the Pi extension seam rather than strip it. (3) Does the agent-rebase experiment prove upstream reconciliation is easy? If so, "own the fork" gets cheaper and the whole calculus relaxes.

---

## Q2 — Codebase strategy

**Recommendation: seed a fresh public repo from the current tree with history reset (no rewrite), then strangler-evolve inside it. Do not deliberate-port-rewrite. Do not resync Pi upstream — own and strip the fork.**

This is my main divergence from the reports, which say "evolve in place; a fresh repo only if the old is frozen." That under-weights the privacy forcing function. The move sequence:

1. **Now (days):** create the public repo by committing the current tree with clean history. This is *not* a rewrite — it's the same code, new history — and it resolves the personal-identity problem immediately, unblocking Marketplace/AGPL Corresponding Source. It also gives the "customize Pie" flow a canonical upstream to fork against.
2. **Then (strangler, ongoing):** inside that repo, cut the accidental complexity. First target: `pi-tui` (~12k lines, imported by **one** render file, effectively dead weight). Then the extension/resource/package/TUI loader layers (~23.5k fork lines), re-plumbing tools per Q1. Keep `pi-ai` (providers) and the core `pi-coding-agent` runtime (session/compaction/auth/model) as an *owned* vendored library — accept that "owning the loop" is a permanent ~50k-line commitment, not a one-time deletion.
3. **Pi fork specifically:** **do not resync** — 98 files / +18k lines, deep changes to `agent-session.ts`/`session-manager.ts`, 32 upstream releases since v0.80.6 with no merge, effectively confirms upstream merges are impractical. Own it, strip the unused layers, replace parts over time. Treat it as a frozen-and-owned dependency whose surface shrinks, not a fork to reconcile.

**Three strongest reasons:**
1. **A history-reset fresh repo is nearly free and is *mandated* by privacy**, and it is a prerequisite for the whole direction (Marketplace release, source-first customization, public contribution). It is categorically different from a rewrite; conflating the two is the reports' main blind spot.
2. **A deliberate port-rewrite throws away the exact knowledge Pie's 1,450 commits encode** (durability, recovery, Windows/lifecycle fixes, leases). Spolsky is right here; writing code is now cheap, rediscovering edge cases is not. The maintainer says "essentially all features are core" — if true, a rewrite just regrows the same complexity unless scope is cut first.
3. **Strangler preserves shippability.** Cutting subsystems one at a time keeps the VSIX building and testable throughout; a big-bang rewrite freezes releases for months with nothing installable.

**Biggest risk:** the maintainer's stated motivation — "too big for what Pie is" — is a *cognitive-load* problem, and strangler doesn't immediately shrink the mental model. **Mitigation:** convert "cut scope" from a feeling into a concrete, ordered kill-list (the complexity audit's "leave behind" list is already 90% of it: TUI, extension/resource/package layers, `extensions/*` shims, VSIX delivery scripts, duplicated session-format code). Executing that list is the actual answer to the fatigue, and it's independent of repo choice.

**Effort:** seed clean repo = **days**. `pi-tui` removal = days. Full strip+re-plumb = 6–12 weeks (shared with Q1). A deliberate rewrite = **6+ months** and is rejected.

**Cheap experiment (days):** seed the clean-history public repo in a throwaway remote, run the full build + test suite against it, confirm nothing depends on history (shallow clones, commit hashes in scripts). This falsifies "history reset breaks something" and de-risks the only irreversible-seeming step.

**Flip questions:** (1) Is line count *measurably* slowing velocity (feature time, bug rate), not just feeling heavy? If velocity is objectively collapsing, a deliberate port-rewrite of a *smaller* core becomes worth re-evaluating. (2) Would the maintainer actually **cut** features to make a small core viable, or is everything sacred? If the latter, a rewrite regrows complexity and only strangler remains sane. (3) Is a second maintainer/contributor arriving? A port is more attractive with 2+ people; solo, strangler is the safer bet.

---

## Challenges to the phase-1 reports

1. **All three under-weight the privacy forcing function** on Q2. They discuss it once ("personal identities requiring a privacy decision") and then proceed to "evolve in place in the current repo." A clean-history public repo is the answer to *both* the privacy problem and the source-first entry point, and it is not a rewrite. I treat it as step one, not a contingency.
2. **`report-complexity-audit` under-counts the re-plumb cost.** "3–5k lines glue" ignores the `tool-and-skill-selection` bridge (~4.4k src / 9.5k tests), `agent-instructions`, and the settings overlap that all shift when the Pi extension/resource layer is removed. The number is directionally right (it's small relative to 400k) but the *subtlety* is in Pi's diverged discovery semantics, not line count. Prototype before committing.
3. **`report-harness-survey` and `report-malleable-precedents` are slightly one-sided on UI.** "No demonstrated demand" for arbitrary UI change is true but circular — there's no installed product yet, so there can't be demand. The maintainer has a clear, defensible vision here. The right move is **one narrow source-level renderer seam now** (cheap, no public contract), which both reports wave off by conflating it with a general runtime GUI plugin API. They're right to reject the latter; wrong to skip the former.
4. **All three frame "keep `pi-ai` + core `pi-coding-agent` runtime" as a savings** without being blunt that it converts Pie into the permanent owner of ~50k+ lines of co-evolved fork. That's the correct *least-bad* choice, but it is a liability to *accept*, not a line to delete. The reports should state the commitment, not imply a one-time cleanup.

**Net:** the reports converge on the right destination (D + strangler). My corrections are: make the clean-history public repo an immediate, mandatory step rather than a fallback; don't trust the 3–5k re-plumb estimate without a one-tool prototype; add one internal renderer seam now; and call "own the fork" what it is — a permanent commitment, not a deletion.
