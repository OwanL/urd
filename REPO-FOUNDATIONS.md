# Repository foundations: source customization and stage-3 review

**2026-11-05 — owner direction plus proposals, not scaffold approval.**
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

**Setup direction (2026-11-05):** build commands may acquire missing
project-declared build tools automatically where practical. After a concrete
comparison of Build fetching missing developer tools versus stopping and requiring
Setup, the owner answered: **“Yeah automatic where possible, we could probably
have some scripts and stuff to make this easier”.** Small convenience scripts fit
that direction; it does not authorize a bespoke tool manager or implementation.
The explanation excluded automatically updating Urd or replacing unrelated machine
programs. Dependency restore remains distinct from tool acquisition; no offline,
zero-prerequisite or fully automatic cross-platform setup guarantee follows.

**Custom-build data direction (2026-11-05):** a newly customized build starts
with separate data rather than immediately opening normal Urd chats/settings.
Exact answer: **“separate data, it is the users responsibility to do with what
they please with the data, perhaps we keep the data next to source? or builds or
similar”.** Users control what they do with their data. On the focused follow-up,
the owner selected **“Keep each copy together”**: a user-chosen writable folder
contains that customized copy's source, generated builds and separate persistent
data. The illustrative `source/`, `builds/`, `data/` names are not fixed layout.
Data stays outside generated output and source-package inputs: clean/rebuild must
leave it intact, and distributions must exclude it. This is separation between
customized copies, not a new empty dataset on every rebuild of the same copy.

This direction concerns launching a custom build, not compiling it; it does not
select ordinary installed-release data paths, credential portability, migrations,
backup or activation mechanics. Keeping files together is not a guarantee that
saved sign-ins work after moving to another machine. User ownership is not
permission for build/clean/package commands to erase or redistribute data.

**Tooling alignment (2026-11-05):** after requesting independent reviews and
then discussing alternatives, the owner said: **“I think the biggest question is
npm vs pnpm, the others such as vite are clear cut in my opinion, vite is good,
and we should probably opt for the more sure .net 10 wouldnt you agree”.**
Use **.NET 10 LTS and Vite** as the revisable working choices. Following a concrete
npm/pnpm setup comparison, the owner answered **“Lean toward npm”**. Record npm
as the preferred route, not an unconditional or irreversible manager selection.
The earlier proposed .NET/Node/pnpm/Vite bundle was not approved as a whole.

**Still open:** Node version, exact SDK/package pins, tool-acquisition mechanism,
physical source/distribution format, exact source/data paths, SDK/cache shipping
and activation mechanics. Test runners, formatters and CI remain engineering work,
not individual owner votes. No production configuration, scripts or scaffold are
authorized by this review.

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

### Overnight tooling candidate and observed limitations

Before the resumed owner review, the recommendation was **.NET 10 LTS CLI +
Node 26 LTS + strict TS/Vite + pnpm**, with locked restores, simple commands and
a distinct distribution check. Its rationale was
[.NET](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core)/[Node](https://nodejs.org/en/about/previous-releases)
support lifetimes and per-project setup, not installed software or toy timings.
The npm preference above supersedes that package-manager recommendation; Node 26
was not selected. The trial did **not** select a test runner, formatter, task
framework or CI.

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

**Alternative examined:** .NET CLI + Vite with **npm and separately provisioned
Node**, now the preferred route following owner review. It has fewer
package-manager/runtime policies and a conventional locked `npm ci` workflow;
Node acquisition/selection belongs to the setup recipe rather than npm. Automatic
setup scripts are allowed, so this route need not impose manual Node management.
It remains documentation-supported, not a duplicate measured stack. A successful
pnpm fixture is not proof of comparative simplicity, and neither route inherently
requires another global version manager.

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

## Resumed review: comparison, limits and smallest handoff

At the owner's request, three isolated read-only reviewers examined toolchain
trade-offs, the no-Git setup/customization workflow, and test/configuration
proportionality. They found the candidate reasonable but **not a clear-cut pnpm
win**, no blocking need for another product decision, and no case for additional
test/task frameworks. They did not run new builds or verify production behavior.

The concrete gaps were automatic bootstrap (the trial reused .NET and required
pnpm before the carried recipe), actual persistent-data isolation, and production
input/configuration scope. Source-folder independence and private-data canaries
are not proof of independent application data. Routine runner/pin choices should
not become another owner questionnaire.

A subsequent narrow official-documentation check clarified the npm/pnpm comparison:

- [npm `devEngines`](https://docs.npmjs.com/cli/v11/configuring-npm/package-json#devengines)
  checks tooling requirements with `warn`, `error` or `ignore`; it does not acquire
  Node. npm comes with Node, leaving acquisition/selection to the setup recipe.
- [pnpm `devEngines.runtime`](https://pnpm.io/package_json#devenginesruntime)
  can acquire declared Node, record its exact version/checksum in the lockfile and
  run scripts with that runtime. This is genuine reusable functionality; shared
  dependency storage is another benefit. It still needs its own bootstrap and
  does not provision .NET.
- [pnpm's installation guide](https://pnpm.io/installation) describes a native
  executable without a Node prerequisite, but currently recommends npm installation
  on Windows because standalone installation can encounter Defender blocks. The
  executable worked in the trial: this is a setup qualification, not a demonstrated
  failure or a reason to require disabling protections. The `globalShims` setting
  controls project-aware global commands; its scope alone does not establish that
  ordinary local builds change global PATH.

**Current recommendation:** prefer npm if a small .NET/Node setup recipe can meet
the accepted automatic-acquisition/copy-isolation behavior. Revisit pnpm if that
recipe becomes disproportionate or its runtime/dependency facilities demonstrably
reduce maintenance. No npm-versus-pnpm clean-setup, size, speed or code-cost
comparison has established a winner. Vite and .NET 10 alignment does not select
Node 26 or preserve the fixture's exact pins.

**Smallest next step:** a short Windows-first customization command/setup plan,
not another broad survey or a production scaffold. It should connect:

1. One documented entry path from distributed source without Git: locate/acquire
   declared tools where practical, invoke them for that copy without replacing
   unrelated machine versions, and restore locked dependencies. Specify one
   version authority per tool and bounded command/configuration scope; do not
   silently apply production root policies to the existing experiments.
2. Fast edit/check/build, with launch distinct from compilation. Launch uses that
   copy's persistent data outside generated output. Rebuild/clean preserves it;
   another customized copy starts separately. This is not an activation or
   data-migration system.
3. A separate distribution operation using matching editable inputs, recipe and
   instructions, including additions/deletions without Git and excluding data,
   credentials, outputs and prior packages. Physical format and capture mechanism
   remain open; the trial did not qualify capture during active edits.
4. The proposed compiler/type checks and fake-model/real-SQLite behavior tests,
   followed separately by one built-browser/backend smoke and a distribution
   roundtrip. Reuse production paths as they exist, not a parallel test-only app.
   Add focused acceptance for copy-data independence, clean/rebuild preservation
   and package exclusion; do not mistake existing canaries for those guarantees.

Any executable follow-up needs a separately scoped handoff targeting the missing
bootstrap/data boundaries rather than repeating the already demonstrated roundtrip.
Fresh-machine and different-version copies remain unqualified; other-OS setup must
be verified in the Windows → macOS → Linux rollout. SDK/cache bundling, ordinary
installed-release data, credential portability and switching to an existing dataset
remain consequential delivery questions, not all gates for the first technical slice.
