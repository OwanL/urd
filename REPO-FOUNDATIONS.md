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

## Evidence — conventional pattern and bounded trial

[Automake distcheck](https://www.gnu.org/software/automake/manual/html_node/Checking-the-Distribution.html) is an established distribution→build/test→redistribute pattern,
not a recommendation to adopt Automake. [MSBuild ZipDirectory](https://learn.microsoft.com/en-us/visualstudio/msbuild/zipdirectory-task) and
[publish metadata](https://learn.microsoft.com/en-us/dotnet/core/project-sdk/msbuild-props#copytopublishdirectory) provide ordinary packaging primitives. [dotnet pack](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-pack) source options target NuGet packages;
[Source Link](https://learn.microsoft.com/en-us/dotnet/standard/library-guidance/sourcelink) is debugging metadata; [Git archive](https://git-scm.com/docs/git-archive) targets a named tree; [npm packing](https://docs.npmjs.com/cli/v11/configuring-npm/package-json#files) excludes
package-manager lockfiles. None alone establishes Urd's complete workflow.

The initial record was documentation-only. A narrow Pie reference inspection found
internal snapshot safeguards, not qualified source-carrying releases. The subsequent
authorized overnight trial used **one disposable hello-sized C#/Preact/guidance fixture**,
not Urd classes, chat, sessions, storage or a production scaffold.

### What the round trip established

Windows x64; experimental SDK **10.0.403**, Node **26.13.0**, pnpm **12.16.1**,
Vite **8.3.5**, Preact **10.29.8**, TypeScript **5.9.3** and Preact preset **2.10.6**.
These are evidence pins, **not owner-selected versions**. Twelve grouped checks passed:

- No Git executable in the child command environment. A source distribution was
  extracted away, built, customized and redistributed; a second extraction rebuilt
  the customized result using documented raw compiler/bundler commands.
- Real headless Chrome observed the compiled backend value, rendered TS value and
  bundled guidance. Used C#/TS/guidance files were edited/added and old used files
  deleted; carried source matched those changes and deletions. Original inputs stayed unchanged.
- Repeated packaging retained **24 source files**, not previous archives, build
  outputs, dependency caches, repository history or synthetic private-data canaries.
  Frozen-lock mismatch and a missing imported TS file failed for the expected reasons.
- Ordinary check/build emitted no source distribution. ZIP/MSBuild packaging was a
  **trial container**, not an archive-versus-folder decision or activation mechanism.

**Capture qualification:** writers were quiescent during a fresh staged copy; source
was archived before restores/build outputs, then that same stage was built. A
barrier paused after UI compilation, before backend compilation. Changing the live
original C#/TS/guidance there did not change the staged binary behaviour or carried
source. This establishes isolation **after capture**, not atomic copying during edits.
Input hashes/inventories checked this known recipe; they are not proof of arbitrary
compiler consumption, protection from intermediate edits or a provenance framework.

### Toolchain implications and recommendation — still revisable

Prefer a coherent candidate of **.NET 10 LTS CLI + Node 26 LTS + strict TS/Vite + pnpm**,
with locked restores, simple discoverable commands and a distinct distribution check.
The rationale is [.NET](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core)/[Node](https://nodejs.org/en/about/previous-releases)
support lifetimes and per-project customizer setup, not installed software or toy timings. Keep the existing fake-model/real-SQLite/browser
strategy; this trial did **not** select a test runner, formatter, task framework or CI.

The trial exposed useful qualifications rather than a zero-setup promise:

- SDK selection follows the **command cwd**, even with an explicit project pathname.
  The recipe anchored commands to its source root; nearest MSBuild props avoided an
  incompatible synthetic parent policy. Shipping locks/configuration/recipe is essential;
  do not silently apply new root policies to existing experiments.
- pnpm provisioned and ran project Node 26 while the explicit machine Node remained 24.
  But **`runtimeOnFail: error` rejected ambient Node 24 even after Node 26 was restored**;
  it was not a simple fail-fast selector for that managed runtime. The working candidate
  used declared runtime provisioning; ordinary pnpm commands may fetch a missing runtime.
  Dependency auto-restore was separately disabled. Do not conflate these policies.
- [pnpm 12 configuration](https://pnpm.io/configuring) differs from older recipes:
  general settings live in YAML; `.npmrc` retains registry/auth-related settings.
  [`globalShims`](https://pnpm.io/settings/other#globalshims) cannot be disabled by project
  YAML. The trial set `PNPM_CONFIG_GLOBAL_SHIMS=false` in its isolated child environment,
  used the native executable directly and never ran setup or modified global PATH/profiles.
  pnpm does not provide the .NET SDK bootstrap.

**Strongest alternative:** .NET CLI + Vite with **npm and separately provisioned Node**.
It has fewer package-manager/runtime policies and a conventional locked `npm ci`
workflow, but requires an explicit answer for per-copy Node versions/clean setup.
That alternative is documentation-supported, not a duplicate measured stack. Choose
on the desired setup/download/maintenance experience; a successful pnpm fixture is
not approval of its defaults, and another global version manager is not automatically needed.

**Limits and evidence location:** existing Windows SDK/runtime, Chrome and Playwright
were reused. Official native pnpm (~24 MB compressed), Node (~41 MB compressed) and
project dependencies were acquired into owned TEMP; the passing run reused caches
warmed during preparation. Separate customized copies used the **same** pins, not a
multiple-toolchain-version test. No fresh-machine, offline, cross-OS, bit-identical,
self-contained-runtime, signing/license-compliance or upgrade/data-compatibility claim.
No real user data, provider call, global installation or installed-host activation.
Temporary fixture/method/logs: `source-roundtrip-798ce188/` under the coordination
TEMP directory; passing `runs/2026-11-04T11-08-38-679Z/RESULT.json`. Fixture code is not
part of this repository. Independent method/scope review found no blocking issue;
owned processes were stopped after verification.

Next: owner review of this coherent workflow versus its alternative; preserve the
open packaging/SDK-cache/activation answers above. Access/onboarding, background
controls, actual shared-host UI reuse, provider credential portability, configuration
ownership and other-OS releases remain broader platform gaps—not solved by this trial.
No production scaffold is authorized here.
