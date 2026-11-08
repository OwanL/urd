# Browser development

Tests and helpers share the repository-wide [hard size budget](testing.md).
Historical test counts below are checkpoint evidence, not the current suite size.

The browser uses Preact components and ordinary state/effects over typed
`SessionClient` and `ProviderClient` HTTP access. Vite builds static assets served
by the same loopback Server; there is no second production server, API proxy or
browser-side provider SDK. Provider credentials remain in the backend.

The workspace has closable, reorderable local tabs, transcript, model and reasoning
selectors, Send and Stop, and [provider settings](provider-management.md#browser-settings).
Snooze remains unavailable. Older-history navigation, Markdown and tool inspectors
are not implemented yet. Closing a tab hides the view; it does not stop work, close
a backend session or delete history. Components and effects stay with their feature;
hooks are extracted for actual responsibilities, not a state framework.

Drag tabs on desktop, or focus a tab and use Alt+Shift+Left/Right to move it. Order is
saved in the existing browser-session workspace without changing the active chat,
drafts or backend sessions. Escape/outside drops cancel; the drag uses a private MIME
type so dropping onto the composer cannot insert a tab ID. Touch dragging is not
implemented. Normal arrow/Home/End navigation and Delete-to-close remain available.
The palette uses neutral black/charcoal surfaces and grey controls; semantic error
colours remain distinct. Rich transcript architecture is a separate workstream.

## Toolchain and checks

Use Node **24.16.0** (`source/ui/.node-version`) and npm **11.13.0**
(`source/ui/package.json`); no global installation or PATH change is required.
Pinned tools are native TypeScript **7.0.2**, Oxlint **1.92.0** with
**oxlint-tsgolint 7.0.2006**, and independently Oxfmt **0.77.0**. Node type definitions
remain **24.19.2**. Rendering/build pins are **Preact 11.2.0** and **Vite 8.3.6**,
without React compatibility or compiler plugins. Keep optional native packages
installed. Node runs tests with native TypeScript stripping; strict compilation is
separate.

From `source/ui/`:

```powershell
npm ci --ignore-scripts --no-audit --no-fund
npm run check
npm test
npm run build
```

| Command | Boundary |
| --- | --- |
| `npm run check` | Formatter check, one combined native type-aware lint pass, then standalone strict compiler; direct CLI chain, no recursive npm calls. |
| `npm run format` / `npm run format:check` | Write formatting / check formatting without changes. |
| `npm run lint` / `npm run lint:fast` | Combined typed lint / syntax-only feedback. |
| `npm run typecheck` / `npm run typecheck:watch` | Strict compiler once / persistent watch. |
| `npm test` | Node unit tests discovered from `tests/**/*.test.ts`; no frontend test framework. |
| `npm run build` / `npm run build:watch` | Bundle once / watch into `source/ui/dist`; neither runs a server. |
| `npm run test:integration` | Real Server and SDK-to-loopback-provider test; requires built UI assets and the Release backend build. |
| `npm run test:browser:host` | Explicit temporary browser fixture host; real Urd plus a credential-free loopback model fixture, never saved accounts. |

Formatting pins width 100, double quotes, trailing commas and two-space indentation.
Import/package sorting is disabled; generated `package-lock.json` is ignored by the
formatter. Generated `dist` is excluded from formatting, lint, compilation and Git.
Lint selects hook, accessibility, promise and a small set of bug rules, not whole
presets. Typed lint does not replace strict compilation: do not use experimental
`--type-check` or a JS-plugin bridge. Node tests remain linted; top-level
registrations explicitly use `void` because Node owns their completion. Async work
inside test bodies remains checked.

Normal named `preact/hooks` imports are qualified for conditional-hook and effect
checks. Aliased and namespace effect dependencies have gaps; do not claim general
Preact alias/namespace support. Keep the control-label rule's default input/textarea
exclusions: overriding them falsely flags valid external `<label htmlFor>`
associations. Unlabeled inputs need browser accessibility checks, not misleading
ARIA labels added to satisfy lint. Static checks alone do not prove accessibility.

Run verification locally. Build the UI before .NET so assets are staged with the
Server, then run the real-server integration test. Existing commit hooks remain
unchanged; no auto-stash, fix, build or test hooks are added.

## Building and inspecting the screen

Run `npm run build`, then restore/build the Release backend using
[the backend instructions](backend-development.md#prerequisite-and-commands), then
run `npm run test:integration`. The Server project copies `dist` into output/publish
`wwwroot`; backend-only builds do not run npm or require a UI bundle. Rebuild .NET
after a changed bundle to stage it. HTML is not cached; hashed assets are immutable.
Static requests still pass the loopback/Host/Origin gate, and API requests still
require the browser marker. Unknown API paths are not redirected to HTML.

The fixture launches the built Server with its own temporary data root and port
`0`, creates a custom no-auth connection to its owned loopback model endpoint, and
refreshes only that fixture's catalog. It exercises the real managed provider, SDK,
execution and SQLite path, not a production fake-client flag. For rendered checks,
run `npm run test:browser:host` and open its printed Urd origin. Ctrl+C or POST to
the printed model origin's `/__shutdown` closes the fixture. Test-control routes
belong only to the fixture process, not Urd. Only owned processes/data are cleaned
up. Set `DOTNET_HOST_PATH` to a specific `dotnet` executable if needed; otherwise it
uses PATH. Explicit fixture termination is not evidence of graceful shutdown.
Neither command authorizes live provider/account requests.

## Shared UI controls

`components/Popover.tsx` owns anchored placement, viewport clamping and dismissal.
It uses the browser's manual top layer with entirely custom markup/styles, not OS
menus or dialogs; current browsers with the Popover API are required. Interactive
panels focus their initial control and return focus on Escape, but outside clicks
keep their destination. Selection controls keep their own keyboard/value semantics.

`Tooltip` accepts formatted descriptive content and links it to the actual control
with `aria-describedby`; interactive details belong in `Popover`. Hints appear on
keyboard focus or brief hover, remain reachable across the gap and dismiss on Escape
or action. `IconButton` combines an accessible label, SVG icon and custom hint. Do not
add native `title` hints or native select menus alongside these controls.

The refinement was checked in isolated Chromium with the real loopback fixture:
model/reasoning selection, focus/dismissal, hoverable hints, empty selection, narrow
overflowing tabs, streaming/Stop, draft retention and jump-to-latest. Desktop, mobile
and 600×400 zoom-equivalent captures passed independent visual review. No live
account requests were made for these checks; screenshots stay in an owned temp folder.

## Transport and selection ownership

- `source/ui/sessions/SessionClient.ts` maps to the
  [session HTTP contract](server-development.md#session-http-contract).
  Construction does no I/O; callers own selection, polling and reconciliation.
- Both clients share `http/requestJson.ts`: the browser marker, no caching, omitted
  cookies, rejected redirects and status-preserving `RequestError`. Provider reads
  use saved connections/catalogs; only explicit refresh initiates discovery.
- Create/Stop/refresh send JSON `{}`; submission fields and opaque list cursors are
  preserved. Selection includes the full secret-free expected connection binding,
  model ID and optional reasoning effort, not lookup by mutable connection ID.
- Only reads accept cancellation. Commands never retry automatically, including
  missing receipts, malformed acknowledgements or disconnects. Tab changes cancel
  obsolete reads, not submitted work.
- HTTP failures preserve status and available JSON/text body, even when reading the
  error body fails. Stop's unavailable receipt stays in the 409 body. No failure
  becomes a fabricated refusal.
- Success requires JSON; Create also requires a nonempty GUID. Other records
  describe the wire contract, not general runtime validation. Receipt matching
  must cover the full original submission before resolving uncertainty or clearing
  a draft. Editing a draft while Send is pending must not let its later receipt
  clear the newer draft.
- Model/reasoning choice is fixed for an execution. Busy Send is steering with the
  active choice; conflicting selection is a durable `selectionLocked` refusal.
  Local unsent choices are not a separate mutable server-settings endpoint.
- `latestLlmSelection` is the initiating choice of the newest execution, active or
  settled. Null represents fresh/legacy history, not a fake model default for new
  Sends. Null reasoning means provider default; explicit efforts are `none`, `low`,
  `medium`, `high`, `extraHigh`. Those are request values, not a claim that every
  model supports them. Current catalogs provide IDs/names, not reasoning capability.

The [context-choice alignment](ARCHITECTURE.md#provider-specific-context-choices)
keeps future provider context modes separate from limits, Urd's context budget and
pricing. This slice adds no context-tier selector, inferred limit or silent history
truncation. Display remains the bounded recent snapshot plus on-demand message
content, not a claim of full-history navigation.

## First workspace qualification

The integrated Windows Release checkpoint passed **591 backend tests**, **85 UI
unit tests**, the full UI static gate, Vite build and the real-server/loopback-SDK
integration test, with no build warnings. Chromium rendered checks covered actual
model/effort delivery, Send/Stop, exact retry and reload reconciliation, foreign
receipts, newer drafts, closing a busy tab, failed/lost Stop acknowledgements and
stopping a successor while an earlier Stop acknowledgement remained pending.

Two-client checks covered selection-lock refusals and accepted work falling out of
the recent message window (using a bounded-response fixture). Reading anchors,
streaming text selection, idle polling, model refresh/search, keyboard tabs,
Enter/Shift+Enter and the IME guard were checked. Desktop, 390 px mobile, overflowing
tabs and a 200%-equivalent viewport passed rendered/visual review. Screenshots stay
local; Pie was only a visual reference, not a source of copied code or styles.
These are manual Chromium fixture checks, not an automated browser suite or cross-browser,
native-IME, live-account or full-history-navigation qualification.

## Historical qualification

The original transport checkpoint passed locked npm installation, strict
compilation, **36/36 HTTP boundary tests**, real-server integration and **296/296
Release backend tests**, without build warnings. That checkpoint used the original
fake-client Server composition; it did not qualify provider execution or render a
UI. The current loopback-provider fixture replaces that composition in integration.

The tooling study used 11 good TS/TSX files (756 lines) on Windows x64. TS7 preserved
seeded strict diagnostics; combined Oxlint caught all nine selected lint seeds.
Oxfmt matched Prettier byte-for-byte on that corpus and was independently
idempotent. Fresh-process medians were 84 ms formatting, 373 ms typed-only lint and
444 ms strict compilation. These warm-cache, startup-dominated measurements are
not a scale, editor/LSP, browser or cross-platform qualification. TS7 lacks the old
compiler API; compiler-API plugins need separate qualification. Oxfmt is 0.x:
keep pins and review upgrades rather than assuming universal compatibility.

The adopted tooling then passed a fresh locked, script-disabled install, the full
static gate, **36/36 unit tests** and real-server wire integration. Fourteen isolated
good/bad checks verified configured diagnostics and formatting, including promises
inside test bodies. Independent review approved. `npm run check` took **1.13 s
median** across five fresh processes (1.08–1.19 s; first measured run 1.39 s),
including npm/shell startup and all three checks. This is a baseline, not a fixed
budget or a claim about future application performance. Linux verification was not run
locally for that checkpoint.

The Preact/Vite preparation passed locked script-disabled installation and a
separate OS-temp TSX fixture: strict compilation, invalid-prop diagnostics, HTML/CSS
bundling and watch rebuilds, plus generated-output exclusion. That fixture alone
is not evidence of the real screen's rendering, behavior or accessibility.
