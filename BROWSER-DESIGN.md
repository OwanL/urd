# Browser foundation — owner decision and bounded evidence

2026-11-04. Design investigation, **not production implementation**. README and
ARCHITECTURE remain authoritative; Session Design owns overall structural integration.

## Decision and recommendation

**Owner selected TypeScript + Preact:** after discussing why it fits Urd, the
owner answered **“Yes, proceed with it.”** Earlier steering valued TypeScript,
Tailwind and Pie's frontend while explicitly inviting alternatives.

Use one browser UI whose components and presentation logic can later run in
desktop/VS Code webviews. Do not carry over Pie's host-owned UI state, Pi runtime
or delivery machinery. Node is a source-build tool, not an additional required
Urd application server. The C# backend remains selected independently.

Why this direction:

- Native browser text, selection, keyboard, scrolling and streaming behavior;
  TypeScript checks the client-side shapes and component boundaries.
- Preact supplies ordinary reusable components and DOM updates without a bespoke
  renderer. It already serves this kind of UI in Pie.
- Sharing means reusing actual components/state, not merely using the same language.
  Host resource URLs, CSP, themes and host actions still need small explicit adapters.
- Fast switching primarily comes from bounded reads, caching and lazy detail,
  **not a claim of uniquely fast Preact rendering**.

Costs: Node plus .NET for source builds; a C#↔TypeScript boundary that needs tests;
Preact compatibility is not universal across React libraries. Prefer React over
accumulating compatibility workarounds if a justified dependency requires it.
This is a revisit condition, not a third framework being developed.

**Not selected:** Tailwind versus CSS, Vite/build-tool pins, state libraries,
transport protocol, desktop host, final appearance, folders or concrete classes.
No production scaffold was created.

### Two approaches examined

| Approach | Finding |
| --- | --- |
| **TypeScript/Preact, browser-owned rendering** | Representative bounded transcript experiment passed. Fits direct browser/webview sharing; extra source toolchain is explicit. Recommended and owner-selected. |
| **Blazor components** | Tiny .NET server-interactive contrast built and switched sessions. Disconnect prevented a UI-only switch while its independent synthetic backend counter continued. Server circuits therefore introduce connection-dependent interaction. WASM can move interaction local, but downloads a browser .NET runtime; not measured here. C#-only source tooling is a real advantage, but insufficient reason to replace the proven DOM approach for Urd. No claim Blazor is incapable or inherently slow. |

Stopped after this architectural tradeoff; did not build a second complete product
mock or survey every JS framework. The Blazor subset is **not a fair framework
performance/LOC comparison**. Existing research had advanced a shared TS/DOM UI
and reserved Blazor; it had not ranked individual JS component libraries.

## Resumed client/access review — 2026-11-05

### Owner outcome: local first, without a new sign-in system

The question explicitly separated the fake-model **technical slice** from the
first **normal-use browser milestone**. Asked whether that milestone needs another
device, the owner answered: **“Yes. local first, then lan, tailscale comes later”.**
The access delivery order is therefore **same-machine → LAN → Tailscale**. This
stages retained remote support; it does not remove shared clients, backend-local
execution, later desktop/editor clients or the Windows → macOS → Linux rollout.

An initial suggestion to approve/remember each browser was **not accepted**. The
owner challenged IP/MAC identity and then the unnecessary account/sign-in framing:
**“Urd account? who said anything about accounts, i was thinking we just keep things
simple, same as pie”.** No Urd account was ever a requirement. After inspecting
Pie's actual source, the revised question was whether to preserve its open-and-use
local flow while trusting programs already running on the PC. The owner answered:

> Keep it simple. We did a lot of research with pie, and trusted authenticated
> localhost was chosen for a reason. We can later remedy this with tailscale,
> this gives users an authenticated remote medium if they so wish, but that comes later

**Scoped decision:** start the backend → open its local UI → use it, without a new
account, browser-pairing ceremony or remembered-browser authorization system.
Local programs are trusted. No IP/MAC identification or browser fingerprinting is
selected. Preserve loopback-only exposure and browser request checks; do not turn
simplicity into accepting arbitrary browser-origin commands.

**Terminology was clarified in the discussion:** Pie's inspected path has no client
credential. Loopback plus request checks establishes local trust, **not client
authentication**; the quoted word “authenticated” is not evidence of an auth layer.
Tailscale can later provide authenticated connectivity, but no tailnet-wide trust,
proxy identity policy, application credential or deployment was selected. LAN's
access policy still needs its own scoped review; Pie's unauthenticated LAN mode
was not automatically adopted.

Provider login remains separate. In this same-machine milestone, the backend and
provider-onboarding browser are on the same PC; credentials remain backend-local.
There is no new Urd account and no approval of a provider route, credential store,
remote callback workaround or live account operation. A phone using an already
configured backend is distinct from signing that backend into a provider remotely.

### Pie baseline inspected, rather than assumed

Read-only source inspection at Pie `cc82abb0f317f31341d3a6f12aa1545f6be1fbea`:

- `application/hosts/browser/settings.ts` and `http/browser-server.ts`: default
  loopback listener; plain local UI URL, no browser credential issuance/handoff.
- `application/hosts/browser/network-access/policy.ts`: peer/Host checks and exact
  served-Origin validation on WebSocket upgrade. Missing, null and foreign
  WebSocket origins fail. Native local programs can supply those headers.
- `application/hosts/browser/http/browser-server.ts` (`handleHttpRequest`,
  `handleUpgrade`): HTTP serves bodyless GET/HEAD assets and health, **not** generic
  chat commands/history; state and commands use the admitted WebSocket. Its HTTP
  checks alone are therefore not a ready-made policy for Urd's proposed HTTP API.
- `application/frontend/transport/client-transport.ts`: the browser connects to
  `/ws`; VS Code uses its webview message bridge. Renderer IDs/generations fence
  delivery, not authentication. Later hosts do not require a browser-cookie design.
- Pie `README.md`, **Run the standalone localhost UI**: explicit LAN opt-in,
  documented unauthenticated/unencrypted access and no automatic firewall change.

This establishes source behavior, not the current installed build or a new runtime
security test. Preserve the useful local UX, not Pie's renderer/coordinator machinery.

### Smallest candidate wire and host boundary

**Proposal, not approved classes, APIs, protocol or scaffold:** serve the initial
UI and backend API at one loopback origin. Keep three responsibility owners:

| Owner | Minimum responsibility |
| --- | --- |
| Core | Authoritative session/history and domain admission; accepted execution outlives every client request. No ASP.NET dependency. |
| Server | Listener/browser request admission; bounded query/command/update mapping; client-subscription cleanup. Do not duplicate session policy. |
| Each UI | Selection, drafts, bounded cached views, reader position and connection freshness. No frontend copy of the backend session hierarchy. |

Concrete flow: select A → show its bounded cached view, if any, as stale → obtain
current saved history/status and separately identified provisional output → switch
to B without stopping A → reconnect by reading current truth. Late A responses
cannot select A or overwrite B. Reconnection does not replay commands; unknown
acknowledgements remain unresolved until reconciled. Busy-Send and recovery policy
belong to [Session Design](SESSION-DESIGN.md), not a new client-side rule here.

HTTP queries/commands plus one bounded view stream remains a reasonable candidate.
Fresh snapshots and bounded coalesced updates avoid a durable event-replay system;
coherent snapshot/subscription capture and saved/live correlation still need real
integration. **HTTP/SSE, header/fetch and WebSocket/SignalR are not selected.**
The access decision does not require a client credential merely to use SSE, nor
make native EventSource the default because it worked in a fixture.

Before choosing transport, make browser admission explicit for **reads, commands
and streams**. A changed HTTP surface must preserve the intended foreign-browser
boundary, not blindly copy Pie's WebSocket-Origin predicate: HTTP reads/streams may
omit Origin, while form/no-cors requests can cause effects despite unreadable replies.
Keep mutations out of GET and reject disallowed requests before effects. One small
engineering option is requiring a non-secret request header on API calls and denying
foreign preflights; this is a browser-request gate, not a credential, but native
EventSource cannot supply it and fetch needs stream/reconnect handling. Exact-origin
WebSocket admission is another candidate with its own connection handling. Qualify
the chosen path rather than adding both, pairing machinery or a general auth platform.

Later desktop/editor work should add only actual host needs—backend connection,
resource URLs and host actions—around the same components and presentation logic.
No desktop host, generic adapter framework or new file tree is needed now. Connecting
from any surface does not move file/shell execution onto that client. Backend
selection must also respect custom copies' separately owned data; being on the same
PC does not imply that two copies should attach to one backend/data instance.

### Evidence limits and bounded next step

[Overnight access evidence](ACCESS-BOUNDARY-EVIDENCE.md) used injected synthetic
credentials, one-event streams and Windows/headless Chromium. It established
cookie port-sharing, native EventSource's header limitation and the no-CORS effect
counterexample—not onboarding, sustained reconnect, LAN/Tailscale or production
access qualification. Its explicit-client-credential recommendation was a proposal;
**this owner round instead chose trusted-local simplicity for the local milestone**.
Cookies/native SSE and token/header fetch retain real engineering costs if later
needed, but neither their fixture success nor the old recommendation selects them.

The existing TEMP `FIRST-SLICE-CLIENT-CONTRACT.md` and
`FIRST-SLICE-IMPLEMENTATION-PLAN.md` remain candidate integration details. Their old
review-sequencing language does not supersede the resumed parallel discussion.
The proposed technical slice is still two chats/two browser clients, fake-model
streaming, real SQLite history, switching/reconnect and explicit settled Stop—not
normal-use readiness. Independent navigation retains the production ≤100 incremental
LOC condition. Client-state/API/fine layout remain revisable; this review grants no
source/config/scaffold or executable-experiment authorization.

**Next:** integrate Lifecycle's scoped input/recovery outcome, then specify and
qualify one transport-appropriate local request boundary once the technical slice
is approved. Include foreign/missing/null origin cases, hostile Host, no-cors mutation
controls and stream/read disclosure alongside actual two-client reconciliation.
Check server effects as well as browser errors; do not claim local-program isolation.
Later normal-use gates include real provider onboarding/storage and backend controls;
LAN/device testing, Tailscale identity/deployment and actual desktop/editor reuse
remain staged work, not prerequisites to this fake-model slice.

**Review verification:** a focused independent review checked the new decision/
proposal boundaries against Urd authority, overnight evidence and the cited Pie
source; no actionable blocker. This round performed document/source review only:
no new build, browser test, live account, network deployment or runtime security claim.

## Local responsibilities — provisional, not approved structure

A proposed small split, with names illustrative:

| Component/responsibility | Owns; does not own |
| --- | --- |
| `Workspace` | Composes navigation and selected view; no agent execution. |
| `Transcript`, message and inspection components | Display, reader position, hover/focus/disclosure; no independent durable truth. |
| `ClientSessionState` | Bounded pages/read models, local selection/drafts and freshness; not the backend session-class hierarchy or the whole application's state. |
| `BackendConnection` | Typed requests, incoming updates and reconnect; not agent lifetime, credentials or business rules. |

Concrete flow: select a session → show its cached window immediately if present →
fetch missing/current data → apply only to the addressed session/revision. A late
response must never select another tab. Components retain local visual state.

**Owner did not approve this split.** “One client-state owner” was too abstract
and was initially understood as a monolith/microservices proposal. Clarified that
it concerns bookkeeping *inside each UI instance*, not server topology, then
stopped the approval loop. Review a concrete example later rather than treating
non-objection as approval. Class responsibilities should precede file layout;
no `src/Urd.Web` or other folder/project split is implied.

This would be a few focused components and state/connection modules, not dozens
of speculative folders, a generic cross-framework layer or a new state framework.
Backend `SessionBase`/`PrimarySession`/`SubagentSession` need not be frontend classes.
Parent closure of independent workers remains Session Design's open product gate.

## Preserve useful Pie behavior, not its implementation machinery

Read-only reference: Pie `cc82abb0f`; no personal sessions/settings/data inspected.
Relevant owners in that repository:

- `docs/contracts/STATE_CONTRACT.md`, **Session Selection**, **Snapshot Recovery**
  and **Multi-Renderer Ownership**: original-history windows, late-selection fences,
  bounded lazy detail, reader-controlled streaming; Pie currently mirrors selection.
- `harness/session-storage/transcripts/transcript-window-contract.ts`: Pie uses
  a 60-row initial window, 120-row pages and ordinary 240-row cap. These numbers
  are reference behavior, **not selected Urd constants**.
- `application/frontend/transcript/tool-call-card/raw-tool-inspector.tsx` and the
  state contract: hover/focus shows captured **Model produced / Model received**,
  not reconstructed display input/output. Preserve this meaning and explicit
  unavailable states; no new analytics system is needed.
- `application/frontend/transcript/tool-call-item.tsx` and
  `detail-subscription-store.ts`: bounded collapsed child preview; full child
  history only on expansion; release the subscription on collapse/unmount.
- `buffered-text-part.tsx`, `use-transcript-scroll-events.ts` and
  `use-transcript-smooth-follow.ts`: no artificial typing backlog; manual reading
  and text selection take precedence over bottom-follow.
- `application/hosts/vscode/package.json`: actual Pie renderer is **Preact**, not
  React proper, with TypeScript/Vite/Tailwind.

The experiment's simple tool-output panel is not qualification of the complete
captured-exchange hover interaction. No removal of that interaction is proposed.

## Executed experiment

All prototype code/evidence is temporary under:
`C:/Users/OwanLazic/AppData/Local/Temp/urd-foundation-streams-6268b517/browser/`.
There was no Urd UI baseline to capture. Screenshots show synthetic after-states
only and were shown inline to the owner, not presented as final styling.

- Windows, Node **24.16.0**, Preact **10.29.8**, TypeScript **5.9.3**, esbuild
  **0.28.1**; .NET SDK **10.0.403** for the contrast. Experiment pins, not final pins.
- Loopback Node fixture generates requested ranges from two **logical 20,000-row**
  histories. It does not store/materialize 40,000 rows or use SQLite.
- 40-row newest windows, at most 120 cached rows/session and two cached sessions.
  No virtualizer was necessary for this bounded plaintext probe; variable-height
  Markdown/code/image rendering may justify one later.
- Two synthetic streams update every 100 ms independently of subscribers.
  Updates carry latest bounded text, not a final proposed delta protocol.
- A logical 2 MiB ASCII tool result is generated/read in 64 KiB chunks on demand;
  child detail is requested only on expansion. These are not 2 MiB model calls.
- Headless Chromium, 1100×800; heavy jobs serialized with `urd:heavy-local-work`.
  Host free RAM was roughly 0.65 GiB/16 GiB. No comparative resource superiority
  is inferred from this constrained machine.

### Timing results

| Path | Samples | Median | Range |
| --- | ---: | ---: | ---: |
| Warm cached session switch, streaming active | 20 | 30.4 ms | 29.2–32.5 ms |
| Client-cold session B switch | 8 | 45.6 ms | 44.8–48.0 ms |
| Page reload → usable content, warm service/browser | 8 | 46.5 ms | 45.5–62.3 ms |
| First browser navigation in initial run | 1 | 204.1 ms | Not a distribution |
| First 64 KiB tool chunk → content | 1 | 34.3 ms | Not a distribution |

Switch timing starts in the actual click handler. Completion checks matching
session text in an element intersecting the transcript viewport, then two
animation-frame callbacks. This measures real content rather than a spinner,
**not guaranteed physical display presentation**. The selected live-text block
is the timing target. Reload timing starts at navigation time origin, includes
asset/client initialization, but is not backend/process/OS-cold startup.

Sample history request resource durations were 1.3–1.6 ms; a first-run pair was
1.5/3.9 ms with fixture range-generation timings of 0.271/0.152 ms. Those are
**synthetic generation**, not SQLite query results. Transport includes JSON bytes;
JSON decoding was not separately instrumented. Rendering/layout/scheduling are
included in end-to-end timing, not individually isolated.

Observed: 197 attached DOM elements for a 40-row selected view; approximately
27 MiB JS heap in a point-in-time CDP sample after repeated navigation, without
forced GC. Fixture RSS was about 60–62 MiB. Neither is full Chromium/process-tree
memory, steady retained memory or a soak/CPU profile. Minified experimental JS
was 22,727 bytes (9,038 gzip), excluding CSS, Markdown, Tailwind and future features.

### Behavioral checks passed

- No tool/child detail requests before inspection; chunks and child content loaded
  on request. Tool second chunk displayed the expected byte range.
- Older-page insertion preserved the visible anchor within **0.375 px**; repeated
  paging stayed at ≤120 rows. Streaming did not move the detached reader (**0 px**).
- Switching two browser pages produced A/B independently. Synthetic progress
  continued after a peer closed and across forced stream disconnect; reconnect
  refreshed the view (observed sequence 1835→1837 in the focused test).
- Alternative mirrored-selection variant changed both browser pages together in
  both directions. This made the navigation comparison executable, not an estimate.
- TypeScript strict no-emit check and bundles passed. An image-capable reviewer
  found no blocking clipping/overflow/readability issue in the two screenshots;
  chunk text can start mid-line, appropriately identified by its byte range.

### Independent navigation cost condition

Compare `mirror.tsx`→`ui.tsx` and `mirror-server.mjs`→`server.mjs` in the temp probe:
raw client diff **8 added / 8 removed** lines; server **1 added / 10 removed**, with
that added server line only changing the static bundle filename. The same common
history, caching and streaming code is present in both variants.

To avoid relying on compact formatting, `navigation-cost.mjs` prints both sources
through TypeScript's printer: client **9 added / 11 removed**, server **1 added /
16 removed**. Thus a conservative **10 added lines**, without subtracting removals,
is below 100 for this tested design. Local navigation replaces backend selection
storage/broadcast; it does not add client registration or draft synchronization.

This demonstrates cheap independence for the prototype, **not a waiver of the
production ≤100 incremental implementation-LOC condition**. Recheck the complete
first-slice diff once subscriptions, lifecycle invalidation and real navigation
are integrated. Do not add a configurable mirrored/independent mode framework.

### Limits and evidence

This does **not** qualify 50/210 live agents, provider traffic, SQLite contention,
Markdown/highlighting, images, long individual messages, slow LAN/Tailscale,
authentication, mobile layout, accessibility, durable edits/closure or host reuse.
Reconnect assumes immutable synthetic stored rows and refreshes stream/revision;
production must reconcile changed/deleted history, not retain stale rows as fresh.
The fixture counter continuing is not proof of real agent execution lifetime.

Evidence files in the temporary directory:
`ui.tsx`, `server.mjs`, `mirror.tsx`, `mirror-server.mjs`, `make-mirror.mjs`,
`navigation-client.diff`, `navigation-server.diff`, `blazor/`, `RUN.md`,
`0002-4e7fa71a-975e-4161-8c06-4e8566250822-timings.json`,
`0003-2afd4f50-3922-45f6-81d3-0178c9b2d832-correctness.json`,
`0001-0f8ecde6-6b90-4659-bb0f-28cef9e7658b-blazor-contrast.json`,
`0002-9bba796c-b52b-4193-ad2f-54da6818aa8d-mirrored-navigation.json`,
`transcript.png`, `tool-detail.png`.
Independent source/evidence review recomputed medians and verified the variant
diffs; no supported blocker for these limited claims. Browser outcomes were not
independently rerun by that reviewer. All owned browser/server processes stopped.

## Closure interaction sketch — policy still open

A disposable follow-up explored two actions, without implementing backend closure:

- **Close chat:** stop/close the containing chat and its bounded helper; keep the
  independent worker chats reachable in their existing tabs.
- **Close chat and workers…:** explicitly name the affected chats, warn about a
  running worker, focus Cancel by default, and show pending closure until stop
  and cleanup are confirmed. This is not an approved automatic cascade rule.

The example deliberately separates creator **Checkout** from assignment requester
**Release**. Release stays open and retains UI audit's recorded result, not its
entire worker conversation. Stopping API review without a result is not displayed
as successful assignment completion. Group scope, provenance display, navigation
placement and the exact controls are illustrative—not new lifecycle authority.

**Owner outcome:** the first policy-card diagrams were rejected as poor UI. The
revised compact tab/menu/dialog example was at most a possible pattern; the owner
again criticised its UI and warned against overfitting before a real UI exists.
Neither independent-worker survival nor warning-then-cascade was selected. Keep
that policy open; stop refining this mockup and revisit through a real UI slice.
This does not reopen the selected frontend stack or settle classes/folders.

Verification: strict TypeScript checking and bundling passed. Headless interaction
checks covered ordinary-close survivors, safe Cancel, pending tabs remaining
visible, explicit simulated cleanup acknowledgement, and retained requester
results. Independent image review caught a missing visible family-close entry
point; an actual menu capture resolved that presentation gap. **This verifies
only a synthetic interaction**, not real stopping, deletion, authority, result
retention, keyboard accessibility or production UI quality. No backend commands
or production scaffold were added.

Local source and evidence:
`C:\Users\OwanLazic\AppData\Local\Temp\urd-foundation-streams-6268b517\browser-closure-33d8ad0d\v2\`:
`RUN.md`, `ui.tsx`, `choice.png`, `after-close.png`, `dialog.png`,
`family-pending.png`, `after-family.png`. The three focused images were shown
inline to the owner. All owned browser/server processes were stopped.

## Integration interfaces, not final wire protocol

- Session summaries and backend capability/status flags remain authoritative.
  Include stable session/message/tool/child identities; never derive run completion
  from stream EOF, a tab selection, missing result or transport closure.
- A bounded original-history page needs coherent rows, cursor, history revision
  and explicit gaps. Fetch original history separately from model context. SQLite
  Validation found count/window inconsistency in its experimental adapter: agree
  an atomic read contract before integrating, not its current preview semantics.
- Lazy tool/model-exchange and child references must support bounded retrieval,
  refresh, unavailable/error and revision invalidation, without nested full-history
  copies. Attachments use backend-served identities, not arbitrary client paths.
- Display updates need session identity, ordering/revision and a bounded recovery
  snapshot. Duplicate/stale updates cannot regress state. On reconnect/gaps, rebase
  from current truth; avoid an ever-growing event replay backlog.
- Commands explicitly address a session. Unknown acknowledgement does not mean
  rejection; reconnect must not blindly replay Send/Stop/edit actions.
- HTTP/stream cancellation disposes the client's read/subscription, **not accepted
  agent work**. Stop is an explicit command. Execution's tool-effect outcome and
  result-recording outcome must remain distinguishable.
- Provider tokens stay backend-side. Provider Access identified ChatGPT's loopback callback
  as backend-machine onboarding: remote-client sign-in is a separate unqualified
  workflow, not permission to expose tokens or substitute a LAN redirect URI.

SSE + ordinary HTTP were sufficient for the experiment. They are not a final
protocol selection or a ban on SignalR/WebSocket where justified. No host adapter,
public network access or authentication bypass was built.

## Source-build prerequisites and first slice

The experiment used existing Node/npm and .NET installations. Local `npm install`
added four packages in roughly 6 s; strict checking and bundling then passed.
First esbuild invocation took 5.50 s; a subsequent bundle took 0.259 s. These are
warm-machine observations, not clean-machine/install/HMR guarantees. The tiny
Blazor project also built with the .NET SDK; its first build reported 6.19 s.
Different inputs mean these are not comparative build benchmarks.

For the selected direction: .NET SDK for backend changes; a supported pinned
Node/npm toolchain and lockfile for UI source changes. Prebuilt browser assets do
not require users to run Node. UI-only builds should not rebuild/restart agent
execution. A checked-in build command and optional ordinary dev-server reload are
preferable to Pie's host-specific publication machinery. Tailwind/Vite can fit;
their exact adoption and packaged-source workflow still need qualification.

**Smallest next step, after integrated foundation agreement:** implement one
browser→ASP.NET→SQLite newest-window read and explicit session switch, then one
live fake-model stream and lazy detail path. Do not copy this disposable single-file
probe into production as its class design.

First-slice acceptance checks:

1. Original history stays readable after compaction; cursor/row metadata remains
   coherent under concurrent appends, suffix edits and closure. No mixed revisions.
2. Measure warm/cold/reload/process-start separately with actual visible history,
   real storage and Markdown/code. Record query/transport/decode/render costs;
   agree budgets from representative runs rather than declaring these probe
   numbers product SLOs. Include large single rows and constrained memory.
3. Switch rapidly during streaming; stale responses never steal selection or
   contaminate another session. Paging, selection, hover/focus and reader anchors
   work together; keyboard inspection can close/retry without fetching all detail.
4. Disconnect/reload all clients while a real backend-owned fake-model run proceeds;
   reconnect recovers changed history and explicit status without command replay.
5. Two clients share backend work but navigate independently only while the full
   incremental implementation remains within 100 LOC; no draft-handoff system.
6. Verify clean local restore/build and UI-only reload. Later host work proves
   component reuse under actual desktop/VS Code resource/CSP constraints; no claim
   of already-qualified hosts. Fan-out qualification remains a separate workload.

Sources: [existing ranking](FOUNDATION-RANKING.md#ui-ranking-the-strongest-architectural-result),
[Blazor hosting](https://learn.microsoft.com/en-us/aspnet/core/blazor/hosting-models?view=aspnetcore-10.0),
[render modes](https://learn.microsoft.com/en-us/aspnet/core/blazor/components/render-modes?view=aspnetcore-10.0),
[VS Code webviews](https://code.visualstudio.com/api/extension-guides/webview).
