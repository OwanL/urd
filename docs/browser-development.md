# Browser development

Tests and helpers share the repository-wide [hard size budget](testing.md); historical
checkpoint totals are not current suite counts.

The browser uses Preact components and ordinary state/effects over typed
`SessionClient` and `ProviderClient` HTTP access. Vite builds static assets served
by the same loopback Server; there is no second production server, API proxy or
browser-side provider SDK. Provider credentials remain in the backend. Live session
changes arrive through a bounded server-sent event stream; the browser then reads slim
snapshot/history metadata and loads message text on demand.

The workspace has closable, reorderable local tabs, transcript, model and reasoning
selectors, Send, Stop and integrated [provider settings](provider-management.md#browser-settings).
Snooze remains unavailable. The UI displays the latest bounded history page and tool status;
older-page navigation, Markdown and tool arguments/results are not implemented. The history
API supports anchored pages. Closing a tab hides the view; it does not stop work, close a
backend session or delete history. Components and effects stay with their feature; hooks are
extracted for actual responsibilities, not a state framework.

Drag tabs with a pointer, or focus a tab and use Alt+Shift+Left/Right to move it.
The held tab follows the pointer; neighbours move aside when its facing edge crosses
their midpoint, without waiting for release. Stable layout geometry prevents jitter;
short sibling transitions respect reduced motion. At strip edges the held tab stays
visible while overflowing content scrolls beneath it. Small movements remain clicks,
and vertical touch gestures do not reorder.

Only release inside the strip saves order in the existing browser-session workspace;
the active chat, drafts and backend sessions are unchanged. Escape, outside release,
pointer cancellation, lost capture, blur and external order changes cancel the preview.
There is no native drag payload to insert into the composer. Normal arrow/Home/End
navigation and Delete-to-close remain available.

Closing a chat tab only hides it. Its record, draft and uncertain Send journal stay in
this browser tab's `sessionStorage` until the browser tab ends; records are not
automatically pruned because there is no chat-delete action and removing a pending
journal could lose an unresolved command. Ordinary draft edits write only that chat's
draft record. A full workspace save folds in and then removes stale draft records; a
newer draft overlay is retained. Command journals synchronously save the complete
workspace before dispatch. Failed reads/writes block commands and offer an explicit
retry. If cleanup alone fails after a successful save, the saved state remains usable
and cleanup can be retried. Storage access failures stay fail-closed until retry succeeds.
Before the supported public data baseline, incompatible or malformed saved workspace/draft
shapes are discarded, not migrated. Workspace version 3 preserves explicit model-family/image
settings in each selection alongside placeholder/derived title provenance, so an actual first
message of `New chat` is not mistaken for an unnamed tab. Version-2 state is discarded.
The palette uses neutral black/charcoal surfaces and grey controls; semantic error
colours remain distinct. Transcript rows render plain text and tool status; richer formatting
and tool argument/result inspection remain future work.

## Markdown rendering

`source/ui/markdown-rendering/` contains a standalone virtualized Markdown pipeline:
`MarkdownWindowAssembler` reads bounded forward/backward semantic pages through
`MarkdownPageSource`, and `MarkdownRenderer` turns manifests and snapshots into HTML
while retaining source ranges. Dependency-aware trimming separates display from support
ranges. Native selection/focus pins defer trimming; complete code lines and table rows
have placeholders and source-anchor geometry, while arbitrary giant paragraphs, lines,
cells and metadata are not bounded. Generation handoff awaits read drain and generation
release before another open or final disposal. This
is still not activated in Transcript or routes. Catch-up can be deferred by selection,
and unmounting loses selection. The standalone durable suite has 196 UI unit tests and
12 Chromium tests; an exhaustive composed handoff probe is temporary evidence, not a
routine test. Run `npm run test:markdown-rendering` from `source/ui/`.

## Workspace composition

`app/Workspace.tsx` connects the behavioral owners and renders the workspace shell.
It does not implement storage recovery, navigation or catalog request lifecycles.

| Owner | Responsibility and lifetime |
| --- | --- |
| `usePersistedWorkspace` | Owns the current workspace and synchronous updates, draft/root persistence, storage failures and explicit recovery. Its stable command access reads the latest state; successful-load identity changes only on read recovery, not ordinary edits. |
| `useWorkspaceNavigation` | Discovers the initial workspace after a successful read, applies deep links, opens/closes/reorders tabs and synchronizes the URL. It calls the existing Create command; it never stops backend work. |
| `useWorkspaceModels` | Owns cached catalog loading, refresh state and invalidation at workspace lifetime, using `WorkspaceModels` for request publication ordering. |
| `useSessionEvents` | Owns the page-lifetime event stream, saved-ID discovery, open/watched registrations and per-tab status/change notices. |
| `WorkspaceCommands` | Owns Create, Send/retry, receipt reconciliation and Stop across conversation switches. Create still journals before dispatch and opens the acknowledged session against current state. |
| `Conversation` | Mounts only for the active chat; composes snapshot observation, transcript, status and composer. Its snapshot hook owns cancellable reads, not command lifetime. |
| `ConversationState` | Derives selection, Send eligibility and execution status from existing facts; it stores no additional state. |
| `ChatTabs` | Owns tab interaction, focus restoration and active-tab scrolling. |

The workspace retains reading positions outside the keyed conversation view and
starts pending-receipt observation only after storage has been read successfully,
including recovery. A hidden chat's uncertain Send is still reconciled; switching
or closing views never cancels a dispatched command. Dependencies are passed
explicitly rather than through a global store or event bus.

## Toolchain and checks

Use Node **24.16.0** (`source/ui/.node-version`) and npm **11.13.0**
(`source/ui/package.json`); no global installation or PATH change is required.
Pinned tools are native TypeScript **7.0.2**, Oxlint **1.92.0** with
**oxlint-tsgolint 7.0.2006**, and independently Oxfmt **0.77.0**. Node type definitions
remain **24.19.2**. Rendering/build pins are **Preact 11.2.0**, **Vite 8.3.6** and
Playwright **1.65.0**, without React compatibility or compiler plugins. Keep optional
native packages installed. Node runs tests with native TypeScript stripping; strict
compilation is separate.

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
| `npm test` | Fast Node unit tests discovered from `tests/**/*.test.ts`; no frontend component framework. |
| `node --test tests/BrowserRegression.ts` | Headless Playwright regression against the temporary real Server and offline loopback model fixture. |
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
execution and SQLite path, not a production fake-client flag. Install the pinned
Playwright Chromium once with `npx playwright install chromium`, then run
`node --test tests/BrowserRegression.ts` for the headless browser regressions. For
manual inspection, run `npm run test:browser:host` and open its printed Urd origin.
Ctrl+C or POST to
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
  settled. Null represents an empty session, not a fake model default for new
  Sends. Null reasoning means provider default; explicit efforts are `none`, `low`,
  `medium`, `high`, `extraHigh`. Those are request values, not a claim that every
  model supports them. Current catalogs provide IDs/names, not reasoning capability.

The [context-choice alignment](ARCHITECTURE.md#provider-specific-context-choices)
keeps future provider context modes separate from limits, Urd's context budget and
pricing. This slice adds no context-tier selector, inferred limit or silent history
truncation. Snapshots contain slim current facts; the history API pages transcript metadata,
and the browser loads visible message content on demand. Older transcript pages remain
available through the API but are not yet navigable in the UI.

## Standalone virtualized Markdown qualification

The pipeline was checked independently of the live transcript: 512 units were
traversed in both directions; peak rendered code content was 49 pieces with 51
contexts, and tables were 200 pieces with 252 contexts. Maximum source-anchor error was
0.422 px for code and 0 px for tables; no task exceeded 50 ms. Visual review approved
the result. A separate composed generation-handoff probe measured 0 px anchor error
and no long tasks; selection/focus pins correctly deferred catch-up. It is temporary
fixture evidence, not live UI, route activation or a routine-suite guarantee. Arbitrary
large paragraphs/lines/cells/metadata remain unbounded, and selection is lost on unmount.

## Earlier workspace qualification

An earlier integrated Windows checkpoint combined the Release backend, UI static gate,
Vite build, real-server/loopback-SDK integration and rendered Chromium checks. Those checks
covered model/effort delivery, Send/Stop, retry/reload reconciliation, foreign receipts,
drafts, closing a busy tab, reading anchors, model refresh/search, keyboard tabs and narrow
viewports. They were fixture checks, not cross-browser, native-IME, live-account or
full-history-navigation qualification. Screenshots stayed local; Pie was a visual reference,
not a source of copied code or styles.

## Historical qualification

The original transport checkpoint used the earlier fake-client Server composition and
predated provider execution and the rendered UI. Current integration uses the loopback-provider
fixture; these historical checkpoint notes do not describe the current test suite.

The tooling study used 11 good TS/TSX files (756 lines) on Windows x64. TS7 preserved
seeded strict diagnostics; combined Oxlint caught all nine selected lint seeds.
Oxfmt matched Prettier byte-for-byte on that corpus and was independently
idempotent. Fresh-process medians were 84 ms formatting, 373 ms typed-only lint and
444 ms strict compilation. These warm-cache, startup-dominated measurements are
not a scale, editor/LSP, browser or cross-platform qualification. TS7 lacks the old
compiler API; compiler-API plugins need separate qualification. Oxfmt is 0.x:
keep pins and review upgrades rather than assuming universal compatibility.

The adopted tooling was checked with a locked, script-disabled install, the static
gate, unit tests and real-server wire integration. Isolated good/bad checks exercised
configured diagnostics and formatting, including promises inside test bodies. These
historical results are not a cross-platform or future-performance guarantee.

The Preact/Vite preparation passed locked script-disabled installation and a
separate OS-temp TSX fixture: strict compilation, invalid-prop diagnostics, HTML/CSS
bundling and watch rebuilds, plus generated-output exclusion. That fixture alone
is not evidence of the real screen's rendering, behavior or accessibility.
