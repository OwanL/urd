# Urd

Urd is a lightweight, first-party agent workspace, starting with software development.
It rebuilds Pie's settled product direction without depending on Pi or another complete
agent runtime. The aim is less code and coupling, not a different product for its own sake.

## Current status

The C# Core runs durable sessions through real provider-managed clients and SQLite.
It supports exact Send retries, input while busy, configured tool continuation, addressed
Stop, bounded history/content reads and provider-reported token usage capture. A Send fixes
its connection, model and reasoning choice for that execution; retrying it keeps the original
submission. Provider-specific context is saved separately from visible text.

The same loopback ASP.NET Core host serves the Preact browser workspace and its HTTP APIs.
The UI has local tabs, drafts, model/reasoning selection, Send/Stop and provider settings.
User messages can be edited inline: Save stops current work, replaces the later conversation
and starts a new response. Editing does not undo tool effects. The current store requires schema 22;
older pre-release databases require an explicit reset, not automatic migration.
History metadata, not-sent inputs and one shared event stream support transcript updates.
Closing a tab hides it; it does not stop work or close its backend session.

Windows file/shell tools, browser navigation through older history and on-demand tool-body
inspection are available. Host startup records interrupted executions without replay; broader
recovery remains unfinished. An explicit after-run queue and Snooze are unavailable. Transcript
virtualization, images and storage caching are active work; transcript records distinguish approved
requirements from delivered behavior. Reopening does not replay work.

## Start here

- [Documentation index](docs/README.md): current guides, decisions and evidence.
- [Backend development](docs/backend-development.md) and [local server](docs/server-development.md):
  pinned SDK, builds, data ownership and run commands.
- [Browser development](docs/browser-development.md): UI toolchain, builds and local verification.
- [Provider management](docs/provider-management.md): supported connections, settings and qualification.
- [Product direction](docs/product-direction.md): constraints and reasons behind the rebuild.
- [Architecture](docs/ARCHITECTURE.md) and [application plan](docs/application-plan.md):
  decisions, responsibilities and remaining design work.

Build the UI before the Server when staging browser assets. Verification stays local;
see [test scope](docs/testing.md). Do not use a user's live profile or provider account
for tests without separate authorization.

## Project rules

Keep first-party code small, readable and responsive. Tests/helpers must not exceed
application source size. Share backend work across clients; retain source-based customization
rather than building an extension platform. Browser delivery comes first, then desktop/VS Code;
OS rollout is Windows, macOS, Linux. Urd is single-user and local-first, with LAN/Tailscale
access planned—not a shared hosting service. There is no Pie-data migration requirement.

Settled Pie behavior is a reference, not a mandatory parity checklist. Preserve useful
capabilities; challenge complexity with evidence rather than silently cutting scope.
Research and old approval records are not permission to implement unrelated proposals.

Urd uses the [GNU AGPL v3 license](LICENSE).
