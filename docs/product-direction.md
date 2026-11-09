# Product direction

This document owns Urd's product intent and strategic constraints. The root README is
an entry point; [architecture](ARCHITECTURE.md) owns detailed decisions, and development
guides describe delivered behavior. Proposals and experiments are not implementation approval.

## Why rebuild

Pie is already a useful product. Urd rebuilds its settled direction because accumulated
complexity, abandoned systems, runtime adaptation and VS Code publication/customization
machinery made Pie difficult to maintain. A fresh start must actually reduce those costs;
a rewrite alone is not evidence of improvement.

Urd is an agent workspace with software development as its first focus, without excluding
other agent work. Pie's sessions, coordination, guidance, tools, providers, context management,
shared backend, analytics and configuration are the starting inventory—not a parity checklist.
Keep useful outcomes while questioning costly mechanisms. Record intentional removals rather
than losing features through incidental implementation cuts.

## Keep the codebase small

This is a strict requirement. Dependencies, abstractions, features and infrastructure must
justify their maintenance cost. Prefer the smallest clear implementation, not compressed
formatting or complexity hidden behind layers. No whole-repository numerical cap is selected;
**tests and helpers must not exceed application source size** under [the counting rule](testing.md).

Responsiveness and low CPU/memory overhead matter throughout the product. Switching sessions
must reveal usable content quickly even with substantial background work. Measure complete
workflows and dependencies; low first-party line counts do not prove low resource cost.
[Architecture](ARCHITECTURE.md) records exploratory fan-out scenarios, not capacity promises.

## Own the harness

Pi will not be a dependency or runtime coupling. Do not replace it with another complete
agent application's runtime, including Codex app-server or Copilot CLI. Use a purpose-built
harness rather than a general framework for arbitrary harnesses. Focused libraries and
selected snippets remain eligible case by case; first-party ownership does not mean
inventing every algorithm.

C#/.NET, ASP.NET Core/Kestrel, shared SQLite, `IChatClient`, the official C# MCP client SDK
and TypeScript/Preact are selected directions. The narrow Urd-owned model/tool cycle avoids
an optional loop dependency whose bounded comparison saved only 20 probe lines; reconsider
if production integration makes that trade-off materially different. Libraries are revisitable
when evidence shows poor fit, not through speculative interchangeable-backend frameworks.

Classes should make responsibility, state ownership and lifetime understandable. Strong
class design does not mean more classes or inheritance. Core owns session/execution/storage
without ASP.NET; Server owns HTTP, hosting and composition. New meaningful boundaries still
need review; routine improvements should follow the established implementation.

## Bundle source with published builds

Matching editable source and build instructions travel with distributables, including
modified source in custom packages. This does not require repackaging on every compile,
choose an archive/embedding format or promise bundled SDKs and dependency caches.

Customization must work on the user's machine without a required hosted build service.
Acquire missing project-declared build tools automatically where practical; small scripts
are welcome. This does not authorize replacing unrelated machine programs or updating Urd.
Offline and zero-prerequisite builds are not promised. Current tool pins and commands live
in the development guides, not in historical tooling comparisons.

Keep each customized copy together in a user-chosen writable folder, with separate persistent
data by default. Data stays outside generated outputs and source/distribution inputs, survives
clean/rebuild and is not reset by rebuilding the same copy. Exact installed-release locations,
credential portability, package format and activation remain open.

Editing source, building and reloading is an acceptable normal workflow. Simple hot reload
is useful, but does not justify an elaborate reload system. Users own their edits and upgrades:
Git, forks and upstream contribution are optional. Urd need not merge arbitrary modifications.
No automatic updater is being designed; agent-guided upgrade notifications remain a proposal.
[Source/build evidence](repo-foundations.md) records the accepted boundaries and bounded trial.

## Deliver the browser client first

Develop Urd from Pie until it is ready for normal use; do not rush self-hosting by cutting
essential behavior. Rebuild UI code and styling incrementally, using Pie as a behavioral and
visual reference rather than copying its architecture or presentation code.

Browser access order is same-machine, then LAN, then Tailscale. Local use should be open-and-use:
trust programs already on the backend PC, without a Urd account, pairing or remembered-browser
approval. Loopback and strict browser-request checks protect this boundary; they do not
authenticate individual local programs or authorize arbitrary web origins. Remote trust
and transport need separate design.

Standalone desktop and VS Code clients remain planned; their relative order and desktop
host remain open. Share ordinary UI components and backend behavior with small host-specific
adapters, verified in actual hosts. A TUI is outside intended scope. OS rollout is Windows,
then macOS, then Linux; minimum versions, Linux environments and detailed parity remain open.
A convenient Windows implementation is not evidence of portable feature coverage.

## Single-user, local-first workspace

Each user runs their own backend. LAN/Tailscale access does not turn Urd into a multi-user
service; shared hosting, tenancy and cross-user permissions are outside scope. Provider
sign-in is direct, with locally managed credentials, not a required Urd account or hosted
token broker. Initial plaintext credential storage is an explicitly scoped implementation
choice, not portable credential security or permission to publish secrets.

## Share the backend across user interfaces

The backend owns agent work. Browser, desktop and VS Code clients are interfaces into the
same sessions, not separate runtimes. File/shell/computer operations target the backend
machine, not whichever device displays the UI.

Desktop/editor clients should attach to an existing backend or start one when absent.
Closing a client leaves the backend running; stopping the backend is explicit. Authorized
work and scheduled follow-ups can continue without a visible UI, consuming usage and
changing files. Closing a session is a separate final action, not the current tab-hide action.

Users need a clear way to find and stop a running backend. A per-user background app with
a tray icon is proposed, not a selected service/toolkit or startup-at-login policy. Do not
turn background operation into a generic process-management platform.

## Start fresh; preserve Urd's own data

Do not import Pie conversations, settings or saved sign-ins. There is no Pie compatibility
layer or built-in importer requirement. This fresh start does **not** authorize discarding
Urd data during later upgrades. Packaging, cleanup and data changes need their own scope.

## Review and owner decisions

Inspect Pie's behavior and contracts before asking the owner to reconstruct them. Preserve
clearly useful settled behavior; ask about consequential ambiguity, new trade-offs or
meaningful removals—not every helper or already settled choice. Bring evidence and a
recommendation in plain language. Feature cuts and new persistence/lifetime/public-contract
changes are not incidental refactoring.

Keep authored guidance, user/project instructions and personal settings separate. Do not
copy maintainer configuration or live data into defaults. Source access is the escape hatch
for deep customization, not a reason for a new override platform. [Code-shape guidance](code-shape-guidelines.md)
and the [application plan](application-plan.md) define the review workflow.

## Experimental source archive

On 2026-11-05 the owner authorized moving the complete `experiments/` tree, including
ignored/generated files, to `C:\dev\scratch\urd-3333d086\experiments`. SHA-256, size,
relative paths and directory sets were verified. Adjacent `ARCHIVE-NOTE.md` and manifests
record provenance; copied root documents there are historical snapshots, not current docs.

The 85 tracked source files remain recoverable from Git commit
`888aff0c7a98e7b0107b305b663bd64978e97b56`. Historical `experiments/...` references use
that former tree; preserve any tested revision specified by an individual report. Scratch
is disposable; generated files are not backed up by Git. No post-move runtime qualification
is claimed, and old commands may assume the original location.

## Reference material

Initial Pie inspection used commit `cc82abb0f317f31341d3a6f12aa1545f6be1fbea`, not a frozen
parity target. Relevant paths **in Pie**, not Urd: `docs/architecture/ARCHITECTURE.md`,
`docs/contracts/`, `harness/agent-instructions/`, `harness/tools/README.md` and
`docs/plans/PIE-PRODUCTIZATION.md`. Check current behavior and qualification before borrowing
an implementation; Pie's constraints are not automatically Urd's requirements.
