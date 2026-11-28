# Browser development

Tests and helpers share the repository-wide [hard size budget](testing.md); historical
checkpoint totals are not current suite counts.

The browser uses Preact components and ordinary state/effects over typed
`SessionClient` and `ProviderClient` HTTP access. Vite builds static assets served
by the same loopback Server; there is no second production server, API proxy or
browser-side provider SDK. Provider credentials remain in the backend. Live session
changes arrive through a bounded server-sent event stream; the browser then reads slim
snapshot/history metadata and loads message text on demand.

For a new profile, a setup dialog opens over the inert workspace, which does not open or create
chats until setup completes. Setup starts with the default working directory. Saving it starts indexing
instructions and skills in the background while the user connects a provider and assigns models.
ChatGPT sign-in opens a tab from the provider choice; the successful callback closes it. Model discovery
runs automatically. Accepting suggestions for all three buckets advances straight to Instructions.
Missing assignments and manual choices open the editor. Instructions and Skills update automatically
if the scan is still running. Changes are saved as you go; refresh opens the first unmet requirement,
or Instructions if the required settings are already saved. The dialog cannot be dismissed early. After
completion it stays closed while the workspace and Settings remain available for later provider,
bucket and directory recovery.

The workspace has closable, reorderable local tabs, transcript, model and reasoning
selectors, Send, Stop and integrated [provider settings](provider-management.md#browser-settings).
Tabs take the header's remaining width beside New chat, Settings and Snoozed chats. Each tab fits within
its scroll strip; activation, title changes and strip resizing reveal the active tab without fighting
a drag. Tabs have a preferred width of 180px and truncate long titles.
The fresh-chat screen shows working-directory controls first, then a
subtle separator and labeled 24h and 7d API cost totals. These are rolling 24-hour and 168-hour windows,
not calendar days.
The composer keeps only session cost, context and working time. Its session-cost popover has Session,
24h and 7d tabs; switching scopes reads that scope on demand without changing the session-cost chip.
When the row cannot fit the session indicators, they move into More without displacing model,
reasoning, Stop or Send/Continue. There is no separate accounting footer. The conversation uses the
available workspace width.
The model picker loads cached catalogs immediately, discovers missing catalogs on startup and
rediscovers after provider changes, without polling or a routine refresh action. With no providers,
the model and reasoning selectors are replaced by an Add provider action that opens Settings →
Providers; search, loading, empty and failure states stay distinct. An otherwise unselected
chat uses the workspace's last chosen or sent model, then the latest human chat model from the
server's saved runs. The picker resolves these against the current provider catalog; if neither
is available, it selects the first available model after discovery. With no catalog models it
stays empty. An explicit chat choice or the chat's own execution model takes precedence.
Setup and Settings → General set the default working directory through a folder browser. General also
lets you turn LAN hosting on or off and copy its addresses. LAN hosting is off by default and remembers
your choice across restarts. Only enable it on a trusted network: connected devices can use Urd
without signing in. The tray and browser change the same setting. General also has a compaction
slider. Select the token readout to enter an exact count outside the slider's steps, or select
Use default to restore 150,000 tokens. Changes apply after Save. Both default and explicit
counts are capped to leave headroom in a known model window.
The shared `usePreferences` hook owns reads and saves. The trigger changes at the next check, while
working-directory changes still apply only to new chats. Settings → Model buckets assigns the
shared small/medium/large model mappings directly, even when a bucket is empty. Only adding a
new provider in Settings → Providers can offer another preview, limited to its models for empty buckets. Preview never saves; only confirmation
applies those exact assignments, and later catalog/provider changes do not rewrite them. An empty
chat with bucket setup missing shows a warning with inline assignment controls. Settings → Prepass
configures independent tool/skill/instruction/title model choices and protected-resource lists.
General groups subagent depth and default providers together, and reads instruction and skill
sources only when their section is expanded. Provider search is available on the provider list,
not on add or detail screens.
Missing models/resources remain visibly unavailable rather than rebinding. Protection pickers
discover resources for the default directory; built-in instructions always remain. Response headers
freeze the model, effort (`medium` shown as a bare label) and start time. Boundary-anchored
preparation rows cover tools, skills, instructions and title, with truthful
running/completed/fallback/cancelled/interrupted outcomes. Live rows stay open when preparation settles so the transcript does not jump. Previously settled rows start collapsed.
All four purposes default to Small and resolve through saved bucket mappings or an exact
provider/model override. Runtime uses saved choices only; unavailable choices are not rebound. If
neither resolves, skip the call with a warning, never use chat; selection failures retain only
protected skills/instructions and all tools. Missing or failed
title generation uses the first-prompt fallback when it yields a title. The disclosure flags
settled issues and lets the reader expand or collapse the rows. Read-only tooltips load detail and resource pages
on demand, including model, elapsed
time, cost (unknown or estimated where applicable), and bounded provider-emitted reasoning and
output previews; reasoning is never inferred. Open tabs receive backend title metadata via
session status, including inactive tabs, without reading their transcripts. Without a published
title they keep the existing placeholder.
Before the first accepted message, the empty transcript centers the default and current
working-directory paths with explicit Edit actions. Edit opens a single directory editor
with breadcrumbs and an editable address, with an explicit Select folder/Cancel decision;
browsing never applies a change.
An unset default resolves to the filesystem root. Changing the current
path affects this chat only. Saving the default from the empty transcript also updates
that chat's current directory; saving it in Settings applies to new chats only. Typing keeps the choice
visible; the composer always uses “Ask Urd”. Acceptance locks the chat's directory,
including when provider setup fails before visible history. Changing a chat's directory
never changes the default.
The shared folder browser uses Zag's Preact listbox for selection, keyboard navigation
and typeahead. Click selects a folder; double-click, Enter or Open browses it. Breadcrumbs,
Up and filtering all follow the displayed location. Select folder applies the selected child
or the current location when no child is selected. Pending reads and uncommitted typed addresses
block selection; failed reads retain the last location and can be retried.
Unknown directory saves block Send until an explicit snapshot read confirms the current
directory; writes are never automatically repeated.
Busy Send remains opportunistic steering. Unclaimed inputs form a quiet, compact queue at the
transcript tail. Once a request claims an input, its preview moves to Preparing, including during
startup and post-compaction pruning. It becomes a transcript message only when the send attempt
starts. Hovering a row or focusing its controls reveals removal and preview actions;
touch devices show these actions permanently. Uncertain removals keep their explicit status and Check
action visible. A small chevron expands long previews on request; claimed inputs cannot be removed.
Removal never stops active work. With an empty composer, an eligible idle conversation offers Continue
instead of Send, without adding a user message. Continue journals its exact operation before dispatch;
reload checks receipts, and an
uncertain result offers explicit check/retry without clearing a newer draft. Stop cancels the
active run and its pending input. The composer keeps its model
and reasoning pickers and Send/Continue visible on one row at every width. Stop appears only
while work is active and is disabled while stopping. More options holds session details; these
never crowd or wrap the toolbar.
Right-click a chat tab or press Shift+F10 on it and choose **Snooze…**. Scroll or drag the day,
hour and minute wheels, or use the arrow keys. The wheels default to 9:00 am local time tomorrow
and show the return time before you confirm. Only idle chats with no pending work can be snoozed.
History, drafts and local tab order are retained.
The header moon lists snoozed chats and lets you restore them early. The deadline survives a
restart; a returning tab is not selected or focused, and no work starts.

The UI displays a bounded, navigable transcript with compact tool rows, on-demand input/output
inspection and Markdown rendering. The history API supports
anchored pages. Closing an idle chat from its tab permanently deletes its backend session;
closing the browser does not. Components and effects stay with their feature; hooks are
extracted for actual responsibilities, not a state framework.

Click a user message, or focus its Edit message action, to edit inline. Enter/Save stops
current work, awaits cleanup, replaces that message and the later conversation, then starts
a response with the currently selected model/reasoning settings. Shift+Enter inserts a
newline; Escape/Cancel leaves history untouched. There is no confirmation or undo. Links
and text selection do not open editing. Opening an editor does not stop work or alter the
normal composer draft. Pending-input and attachment editing are outside this feature.

`MessageEditing` owns inline drafts and exact Save journals through workspace persistence,
independently of mounted/virtualized rows. A missing acknowledgement stays uncertain; receipt
lookup and explicit Retry save preserve the original operation, text and model. Pending
Save blocks new Send, and cancellation is unavailable once Save is dispatched. Reload checks
receipts rather than blindly repeating edits. Workspace shape **7** discards incompatible
older saved browser state under the pre-release policy.

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

The composer shows compact context used/limit and accumulated agent working time (idle gaps excluded).
Context is the latest request's reported input + output tokens, or an estimate immediately after
compaction; pending input and in-flight output are excluded. Missing usage/limits display `?`.
Working time appears before cost and context on the right once work starts. It survives reload and
history edits; interrupted runs have unknown duration, displayed as a lower bound rather than
counting host downtime. Only the visible working chat ticks locally.
Busy tabs replace Close with a pulsing activity dot and ignore Delete. A background completion
gets a checkmark until opened; hovering or focusing a completed tab reveals Close. Disconnected updates
do not create false completion markers or add tab icons. Completion markers last for this page lifetime.
A disconnected page shows its server address and reconnects automatically. New chat and Send
wait for the live connection; drafts remain editable. Intentional background suspension is not
reported as a connection failure. Initial discovery failure offers an explicit connection retry;
uncertain commands are never replayed by reconnecting.

The close button and Delete request permanent closure without a dialog. If a run stopped but its
final status could not be saved, closure is available only once the server confirms cleanup.
The workspace removes its tab, draft and chat record only once closure is confirmed.
The workspace keeps open chats' local state in this browser tab's `sessionStorage`.
Ordinary draft edits write only that chat's draft record. A full workspace save folds in and then removes stale draft records; a
newer draft overlay is retained. Command journals synchronously save the complete
workspace before dispatch. Failed reads/writes block commands and offer an explicit
retry. If cleanup alone fails after a successful save, the saved state remains usable
and cleanup can be retried. Storage access failures stay fail-closed until retry succeeds.
Browser state is separate from the backend database: incompatible or malformed saved
workspace/draft shapes are discarded, not migrated. The current shape in
[`WorkspaceState.ts`](../source/ui/workspace/WorkspaceState.ts) preserves explicit model metadata
alongside placeholder/generated title provenance, so a generated backend title is not
mistaken for an unnamed tab. Incompatible earlier state is discarded.
The palette uses neutral black/charcoal surfaces and grey controls; semantic error
colours remain distinct. Transcript text uses the Markdown renderer. Tool icons open a shared
inspection card by hover, click or keyboard; hover alone preserves composer focus. The card
refreshes when a running call finishes. File rows offer Copy path and Reveal in Explorer.

Subagent calls use single-line rows showing the child title, model, elapsed assignment time, and
cost including descendants. Visible rows refresh pending titles and live metrics. Selecting a
resolved child opens its read-only transcript within the primary tab, including after the child
completes. The elapsed-time control opens the report for failed or unresolved calls. Breadcrumbs
navigate back to the primary or an ancestor, and nested children open in turn. The tab's agent menu
exposes live descendants by hover or its explicit control. The child composer displays its own
model, working time, cost and context when available. Messages, editing and manual compaction are
unavailable. Live child views refresh while visible; completed transcripts remain available
through their parent call.

A pending `ask_user` replaces the tab’s working dot with a steady amber `?` without selecting
or moving the tab. It stays until the last pending question is answered or withdrawn; a muted
`?` shows the last known state while updates are paused. The question panel shows one question
below the transcript and above the composer. Use its arrows to switch between pending questions. Suggested
answers submit on click; custom answers are always available without touching the composer draft.
Only the primary agent can ask the user. Reload and reconnect read pending questions from the
running server; they do not resubmit an answer.

## Markdown rendering

`source/ui/markdown-rendering/` contains a standalone virtualized Markdown pipeline:
`MarkdownWindowAssembler` reads bounded forward/backward semantic pages through
`MarkdownPageSource`, and `MarkdownRenderer` turns manifests and snapshots into HTML
while retaining source ranges. Piece pages can include same-generation ancestry and table
context descriptors within their shared 16 KiB / 256-record limit. Missing descriptors and
variable metadata still use separate bounded reads. Dependency-aware trimming separates
display from support ranges. Native selection/focus pins defer trimming; complete code
lines and table rows have placeholders and source-anchor geometry, while arbitrary giant
paragraphs, lines, cells and metadata are not bounded. Generation handoff awaits read drain
and generation release before another open or final disposal. `MarkdownClient` adapts
the page-owned HTTP lease API, normalizes Core numeric enums and awaits explicit
release, including failed opens. Normal startup composes the bridge-backed renderer and activates Markdown
in `Transcript` with explicit limits. Catch-up can be deferred by selection, and unmounting
loses selection. Fast unit tests cover the model, paging and handoff logic. Browser suites
and their synthetic renderer harness were removed by owner decision on 2026-11-11;
real layout, selection, focus and clipboard behavior no longer have automated browser
coverage. Earlier composed browser probes remain dated evidence, not current tests.

Use `![Before](<C:/path to/before.png>)` in an assistant reply to show a local
screenshot inline. Relative paths use the session's working directory. Urd
captures standard PNG, JPEG and WebP files while the reply streams and keeps a
copy for history; missing or unsupported files leave an alt-text fallback.
This does not apply to external URLs, user messages, or tool-output text.

## Source layout

`source/ui/` is organised by feature, mirroring the backend's vocabulary. Each folder owns
its components, state, hooks and styles. A folder depends on what it composes, never on its
callers: only `main.tsx` imports `workspace/`, only `workspace/` imports `chat/`, and the
shared features (`sessions/`, `providers/`, `preferences/`, `usage/`, `markdown-rendering/`,
`controls/`, `http/`) import neither. Subfolders of `chat/` may use each other; the transcript
renders editable messages and prepass rows inline.

| Folder | Owns |
| --- | --- |
| `workspace/` | Application shell, persisted workspace state, commands and navigation; `tabs/` holds the tab strip. |
| `chat/` | One chat's view: `composer/`, `working-directory/`, `message-editing/`, `prepass/` and `transcript/` (`reading/`, `viewport/`, `navigation/`, `tool-calls/`). |
| `sessions/` | Session HTTP client, event stream and their hooks; `inputs/` and `transcript/` hold contract types. |
| `markdown-rendering/` | Virtualized Markdown pipeline, with `windowing/` and `images/`. |
| `providers/`, `preferences/`, `usage/` | Their clients, contract types and feature UI; `settings/` composes the settings pages. |
| `controls/`, `folder-browser/`, `http/` | Shared popover/icon controls, folder browsing and request helpers. |

`tests/` mirrors this tree; shared fixtures and servers live in `tests/support/`.

## Workspace composition

`workspace/Workspace.tsx` connects the behavioral owners and renders the workspace shell.
It does not implement storage recovery, navigation or catalog request lifecycles.

| Owner | Responsibility and lifetime |
| --- | --- |
| `usePersistedWorkspace` | Owns the current workspace and synchronous updates, draft/root persistence, storage failures and explicit recovery. Its stable command access reads the latest state; successful-load identity changes only on read recovery, not ordinary edits. |
| `useWorkspaceNavigation` | Discovers the initial workspace after a successful read, applies deep links, opens/closes/reorders tabs and synchronizes the URL. It calls the Create command and coordinates permanent closure of idle chats. |
| `useModelOptions` | Owns cached catalog loading, missing-catalog discovery on startup, rediscovery after provider changes and explicit error retry at workspace lifetime, using `ModelOptions` for request publication ordering. |
| `useSessionEvents` | Owns the page-lifetime event stream, saved-ID discovery, open/watched registrations and per-tab status/change notices. |
| `WorkspaceCommands` | Owns Create, Send/Continue journals and retries, receipt reconciliation, pending-input removal and Stop across conversation switches. Create still journals before dispatch and opens the acknowledged session against current state. |
| `Chat` | Normal startup retains three recent chats, including the active chat; focused or selected views stay mounted beyond that limit, and other chats unmount. It composes snapshot observation, transcript, status and composer. Its snapshot hook owns cancellable reads, not command lifetime. |
| `ChatStatus` | Derives selection, Send eligibility and execution status from existing facts; it stores no additional state. |
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
| `npm test` | Node unit tests discovered from `tests/**/*.test.ts`; no Chromium launch. For one file, use `node --test path/to/file.test.ts`. |
| `npm run test:browser` | Chromium checks for browser viewer input/layout and selected live UI behavior; requires Playwright Chromium (`npx playwright install chromium`). |
| `npm run build` / `npm run build:watch` | Bundle once / watch into `source/ui/dist`; neither runs a server. |
| `npm run test:integration` | Real Server and SDK-to-loopback-provider test; requires built UI assets and the Release backend build. |
| `npm run dev:browser:host` | Explicit temporary browser fixture host; real Urd plus a credential-free loopback model fixture, never saved accounts. |

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
execution and SQLite path, not a production fake-client flag. For manual inspection,
run `npm run dev:browser:host` and open its printed Urd origin. This starts a development
host, not tests.
Playwright also supports the manual `tests/support/TranscriptBaseline.ts`
diagnostic, not routine verification. Its raw-SQL seed still requires schema 16, while
[the schema owner](../source/backend/harness/storage/DatabaseSchema.cs) is at version 29
(as checked 2026-11-17), so it aborts before the workload. Requalify its seed SQL and
version guard together before using it as performance evidence; changing the guard
alone is not qualification. Install its pinned Chromium explicitly before running it.
Ctrl+C or POST to
the printed model origin's `/__shutdown` closes the fixture. Test-control routes
belong only to the fixture process, not Urd. Only owned processes/data are cleaned
up. Set `DOTNET_HOST_PATH` to a specific `dotnet` executable if needed; otherwise it
uses PATH. Explicit fixture termination is not evidence of graceful shutdown.
Neither command authorizes live provider/account requests.

### Prepass browser acceptance — 2026-11-17

An isolated real Server/SDK/SQLite host was exercised in headless Chromium at 1280×900
and 390×844, using owned credential-free loopback providers and a temporary Git project.
Checked model bucket/override persistence, unavailable models/resources, failed-save rollback,
keyboard protections/full-path details, running/completed/fallback/cancelled rows, Stop before
main startup, and main output/draft preservation while title generation was held. Protected
instructions bypassed selection and reached the main request. Screenshot review found no
remaining clear layout issues after removing duplicate labels and truncating secondary paths.
A failed catalog read now exposes error/Retry outside disabled pickers. Checkbox tooltips
opt out of click dismissal: rerendering controlled inputs between native click/change events
had reverted keyboard toggles. Repeated Space toggles and persisted IDs were rechecked.
Preference saves restore a still-visible control only when focus fell to the document;
delayed-write checks confirmed that moving focus or closing Settings is not overridden.
This is a dated manual check, not retained browser coverage or live-model qualification;
no saved credentials, live profile or running user host were used.
The dedicated Prepass tab was subsequently checked at both widths, including three-tab
keyboard wrapping, focus on reopen, General draft preservation, saves across tab changes,
catalog retry and reload persistence. Resource discovery made no reads while inactive.
Fresh 680×560 transcript captures show running/completed rows and delayed title publication.
Bucket allocation was then separated into Model buckets, leaving purpose choices and
protections in Prepass. Four-tab navigation, reload persistence, catalog retry and Specific
model drafts across bucket saves were checked. A real prepass routed skills through the
Medium mapping to a second loopback provider while tools/instructions used Small and title
used its specific override. All three bucket enums are covered by the resolver test theory.
The narrow choice column sizes to its content so Specific model remains fully readable.

## Conversation feedback

Execution feedback belongs in the scrollable transcript, directly after the latest entries,
not in a separate footer above the composer. Followers stay at the tail; readers keep their
position when feedback changes. Show one run status at a time. A recovery fault explains why
sending is blocked; an ordinary admission block does not imply a failure or require a restart.

The Context control reveals **Compact** on hover or keyboard focus and starts compaction directly;
its descriptive tooltip retains context details and feedback, also reachable from More when
context indicators overflow. When the server permits it, a request
made during work queues until execution and cleanup finish. The control shows queued and running
states from the session snapshot; an acknowledged queue does not block steering. Hover or focus
**Queued** to reveal **Cancel**. After compaction starts, use **Stop** instead. Send stays available
during compaction. Prompts appear in the queue and wait until compaction finishes. During manual
compaction the first prompt starts the next run; later queued prompts can be removed individually.
Stop cancels both the compaction and its queued prompts. The control leaves composer drafts
unchanged and does not automatically repeat a no-op or failure. The
browser control has no focus-instructions field; the HTTP operation still accepts optional focus.
Successful manual or automatic compaction adds a quiet **History compacted** entry; its summary
and token counts load on demand. While a response is preparing, any preparation rows already
shown under that marker are omitted from the transcript tail, which keeps the pending response
header. No-op and failure feedback is shown without adding a history boundary. There is no
not-sent input inspector in the workspace.

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
- Both clients share `http/Requests.ts`: the browser marker, no caching, omitted
  cookies, rejected redirects and status-preserving `RequestError`. Provider reads
  use saved connections/catalogs. Startup discovers missing catalogs and provider changes
  rediscover them; Retry repeats failed discovery. No timer or picker-opening refresh runs.
  Provider settings can also explicitly discover a selected provider's catalog.
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
  Sends. Reasoning defaults to `high`, including when the submitted effort is null.
  The selector offers `none`, `low`, `medium`, `high`, `extraHigh`, with no provider-default
  option. Those are request values, not a claim that every
  model supports them. Current catalogs provide IDs/names, not reasoning capability.

The [context-choice alignment](ARCHITECTURE.md#provider-specific-context-choices)
keeps future provider context modes separate from limits, Urd's context budget and
pricing. This slice adds no context-tier selector, inferred limit or silent history
truncation. Snapshots contain slim current facts; the history API pages transcript metadata,
and the browser navigates the bounded transcript while loading visible message content on
demand.

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
