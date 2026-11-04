# Foundation review: decisions for the next session

**2026-11-04 evidence, with subsequent owner-review outcomes marked below.**
Recommendations remain proposals; no production implementation is authorized.

The full README/ARCHITECTURE was reviewed before choosing four investigations.
No Urd implementation, production configuration or scaffold was added. Existing
technology, Core/Server, source-distribution and retained-feature decisions stand.

## What changed our understanding

| Finding | Practical consequence | Evidence and limit |
| --- | --- | --- |
| **Reload can change what the model sees.** Ordinary message JSON changed previously sent literal tool text into quoted text, and native text/image blocks into serialized object text. | Saving IDs and readable messages is insufficient. Qualify exact already-sent results through the eventual context owner; keep `IChatClient`. Typed reconstruction and necessary native-result retention remain alternatives. | [Context evidence](CONTEXT-LIFETIME-EVIDENCE.md): 22 assertions in the final three-request local adapter fixture. The replay control preserved ordered content/values, not identical HTTP bytes. No live model/cache claim. |
| **Localhost is not an access policy.** Cross-origin requests carried Strict cookies; cookies also reached another port. A no-CORS-only control still changed server state. | Protect reads, commands and streams. Native EventSource works with cookies, not custom Authorization headers; header-based fetch streaming is an alternative with its own token/reconnect work. | [Access evidence](ACCESS-BOUNDARY-EVIDENCE.md): 66 assertions, real headless browser plus server-side controls. Injected synthetic credentials—not onboarding, remote access or production authentication. |
| **Source customization worked without Git.** Extract/build, edit/add/delete backend/UI/guidance, redistribute, extract/rebuild produced the expected visible changes and carried source. | Conventional packaging can support the desired workflow without a self-rebuilding application. Keep ordinary builds separate from distribution checks. | [Source/build evidence](REPO-FOUNDATIONS.md#what-the-round-trip-established): 12 grouped checks. A post-capture edit did not affect staged output/source; copying during edits was **not** qualified. Warm Windows tools/caches, not fresh-machine/offline/other-OS proof. |
| **Durable intent and retained content have different lifetimes.** A due request known not to have started differs from a possibly executed command; a needed shared result differs from its worker's transcript. | Preserve automatic due work without replaying uncertain effects. Worker closure must not delete another open chat's result/attachment—or retain the entire closed transcript to save one result. | [Durability/reference ledger](DURABLE-BOUNDARIES-EVIDENCE.md): source-grounded failure analysis, **no new runtime test**. Includes attachment publication/deletion races and compact final usage. |

## Current owner review

**1. Setup/custom-copy direction agreed; toolchain remains open.** The owner
accepted automatic acquisition of missing project-declared build tools where
practical, with small convenience scripts. Customized copies default to separate
persistent data and keep source, builds and data together in a user-chosen writable
folder. Data survives clean/rebuild and stays outside source/distribution inputs;
exact paths, ordinary installed-release policy, sign-in portability and activation
are not selected. [Recorded answers](REPO-FOUNDATIONS.md#owner-outcome).

Recommend .NET 10 LTS CLI, Node 26 LTS, strict TypeScript/Vite and pnpm as a
**revisable candidate**. Project-managed Node is useful for customized copies;
it also means ordinary commands may download a missing runtime. The trial found
that a simple fail-fast configuration did not select the already-restored runtime,
and pnpm's global-shim setting was not project-configurable. Acquisition mechanics
still need qualification; the new direction does not permit hidden machine changes.

Strong alternative: the same .NET/Vite approach with npm and separately provisioned
Node—fewer manager/runtime policies, more explicit version/setup responsibility.
The trial did not compare different-version copies or a clean machine. Exact pins,
format, SDK/cache shipping and activation remain open. Test strategy remains
fake-model + real-SQLite behavior tests and one built-UI/backend smoke; selecting
routine runners/formatters need not become another owner voting exercise.

**2. Access sequence agreed; first-slice details remain under review.** The owner
selected same-machine browser use first, then LAN, then Tailscale. Same-origin
hosting and backend-machine provider onboarding remain recommendations, not answers
inferred from that sequence. Remote stages still need connection trust, client
enrollment and real-device qualification. A remote browser's provider callback
returns to that device's loopback, not the backend. Provider login is not client
authentication to Urd.

The existing first-slice proposal remains two chats/two browser clients with fake
model streaming, SQLite history, reconnect and settled Stop. It proposes rejecting
new Send while busy rather than silently queueing; steering is deferred, not removed.
Approve or adjust that bounded scope before implementation. These experiments did
**not** implement or qualify that integrated slice, select HTTP/SSE over SignalR,
or approve fine layout/APIs/schema.

## Wider decisions remain visible—not all gates for the first slice

- **Before live context/reset behavior:** decide how explicit model/guidance changes
  take effect and when capability selection refreshes; preserve readable originals
  and current permissions. Context reset and reselection are different operations.
  The owner has accepted continuing an explicit idle provider switch without a
  blocking confirmation solely for nontransferable private reasoning; no permission
  to lose visible content or replay tools follows.
- **Before uncertain-effect recovery:** consider explicit move-on with an action
  still marked unknown versus blocking that chat until reconciliation. Never infer
  automatic replay. This needs a concrete user flow, not a new persistence engine.
- **Before real retained data:** qualify shared attachment cleanup, coherent bounded
  history reads and summary finalization. Storage wording currently mixes allowable
  recent loss with identical acknowledged replay; clarify provisional progress,
  committed records and crash class without inventing stronger durability promises.
- **Before normal background use:** offscreen attention, discoverable stop/shutdown,
  credential-store availability and startup-at-login need concrete treatment.
  Independent-worker closure still needs meaningful UI context, not another abstract mock.
- **Later qualification:** delegated capability/fallback fidelity, actual shared UI
  host reuse, rich-history/resource costs, native tools and macOS/Linux releases.
  No live-provider entitlement/cache, portable credential-store, packaged-daemon
  or substantial-agent-capacity claim was established overnight.

The four focused reports were independently reviewed within their stated limits.
Research recommendations do not override the owner or turn fixtures into production
architecture. No live accounts/private data, global setup, push or activation was used.
