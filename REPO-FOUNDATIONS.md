# Repository foundations: source customization and stage-3 review

**2026-11-04 — owner direction plus proposals, not scaffold approval.**
[README](README.md) owns product intent; [Session Design](SESSION-DESIGN.md)
records the selected, revisable Core-without-ASP.NET / Server → Core boundary.
This review does not change those responsibilities or select frontend internals.

## Owner outcome

The owner rejected choosing tools/versions because they are installed locally:
“No, do not consider what we have locally on this environment, this is nonsensical, we should cater things to what is best, not what we have”.
Machine inventory is execution feasibility information, **not a selection criterion**.

The discussion then moved from tooling labels to long-term customization. The
owner envisioned source packaged with builds and modified source carried into
subsequent builds, but explicitly cautioned: “do not overfit. My design / intuition
may not be better than established patterns, this is not my area of expertise”.

**Accepted direction:** matching editable source and build instructions travel with distributable
builds; custom packages carry modified source too. Exact answer: **“Yes—record that direction”** to:
“Shall we record matching editable source and build instructions travelling with distributable builds as the direction, leaving the physical packaging details open?”
The question excluded repackaging on every compile, SDK/cache bundling and selection
of an upgrade/activation system. None of those were approved.

**Not selected:** archive versus unpacked files, executable embedding, editable
source location, setup automation, package manager, bundler, runtime versions,
test runner, CI, distribution formats or activation mechanics. Earlier npm/Vite/
plain-CLI recommendations and restrictions on extra tooling remain proposals,
not accepted constraints. Stage 3's tooling baseline is therefore still open.

## Research recommendation — conventional source distribution

Use ordinary packaging plus matching editable source, not a self-rebuilding runtime.
Prefer a source archive within the distributable package;
an unpacked tree is a viable alternative for discoverability. The archive needs
extraction; the tree has more files and can blur the distinction between carried
source, ongoing edits and installation files. Installed locations may be read-only.
Embedding source inside an executable adds extraction/packaging coupling without
a demonstrated advantage here. **The owner accepted the goal, not this format.**

Keep quick compile/check/watch separate from creating a distributable. Package
current source, including additions and uncommitted edits, without requiring Git.
Include the build/package recipe itself so the process works from the distributed
source. Keep outputs and live application data separate; packaging is not activation.

Important implementation checks, not a selected capture design:
- Build and package from the same complete inputs, rather than zipping a working
  directory after compilation and assuming it still matches. Directory copying
  is not atomic during edits; capture consistency needs an explicit boundary/check.
- Include authored code, assets/guidance, projects/manifests/locks, required build
  configuration/scripts, useful tests/instructions and licenses. Git tracking or
  `.gitignore` alone is not a completeness or privacy policy.
- Keep prior packages/source archives, generated outputs, dependency/SDK caches,
  repository history and personal data/credentials/logs out of editable-source inputs.
  Separate cache/tool payloads remain open; required external inputs cannot be silently omitted.
- Source availability is not offline dependency availability, indefinite rebuild
  support, bit-identical reproducibility or proof of license compliance. Covered
  dependency-source obligations need review; lockfiles alone may not satisfy them.
- Custom packages must not require Urd's private release-signing credentials.
  Switching builds and database compatibility remain separate design work.

## Tooling and tests — candidates to evaluate against that workflow

[Vite](https://preactjs.com/guide/v11/getting-started/) and [Rsbuild](https://rsbuild.dev/guide/framework/preact) support Preact; Vite is Preact's documented starting point
and needs [separate TypeScript checking](https://vite.dev/guide/features.html#typescript). npm offers locked clean installs;
[pnpm](https://pnpm.io/package_json#devenginesruntime) adds dependency isolation/shared storage and project-runtime locking/provisioning,
with additional bootstrap/linked-layout considerations. Neither supplies the complete
source workflow automatically. Avoid duplicate version-management owners; standard
commands, modest wrappers or established task/version tools can earn their cost.
Select versions on support lifetime and actual dependency compatibility.

Proposed conventions remain strict TypeScript, C# nullability, compiler/analyzer
checks, standard formatters, clear responsibility names and reviewed dependency
locks/upgrades. Scope production SDK/MSBuild/package/format settings deliberately:
root policies and command working directories can otherwise change experiments.

Proposed tests: fake `IChatClient` behaviour; real SQLite guarded mutation/reopen
flows; one real browser/Kestrel/SQLite smoke using the fake model. Keep fast editing
checks separate from browser downloads, integration and packaging checks. Test the
actual production paths, not just experimental fixtures; runner choice is still open.

**Source-workflow acceptance:** start from distributed source without `.git`, build
with documented prerequisites, make an observable edit/addition, package again,
and verify both changed behaviour and corresponding carried source. Check missing
inputs, accidental data inclusion, output/archive recursion and source mismatch.
Begin with one bounded round trip, not a large reproducible-build infrastructure.

## Evidence and next step

[Automake distcheck](https://www.gnu.org/software/automake/manual/html_node/Checking-the-Distribution.html) is an established distribution→build/test→redistribute pattern,
not a recommendation to adopt Automake. [MSBuild ZipDirectory](https://learn.microsoft.com/en-us/visualstudio/msbuild/zipdirectory-task) and
[publish metadata](https://learn.microsoft.com/en-us/dotnet/core/project-sdk/msbuild-props#copytopublishdirectory) provide ordinary packaging primitives. [dotnet pack](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-pack) source options target NuGet packages;
[Source Link](https://learn.microsoft.com/en-us/dotnet/standard/library-guidance/sourcelink) is debugging metadata; [Git archive](https://git-scm.com/docs/git-archive) targets a named tree; [npm packing](https://docs.npmjs.com/cli/v11/configuring-npm/package-json#files) excludes
package-manager lockfiles. None alone establishes Urd's complete workflow.

Read-only official-document research and focused independent reviews only. A narrow
Pie reference inspection found internal snapshot safeguards, not qualified source-carrying releases.
**No build, restore, installation, executable probe, provider call or source-package round trip was run.**
Next: use the accepted source-customization direction to finish the bounded tooling/command plan;
retain open packaging/activation choices. No production scaffold is authorized here.
