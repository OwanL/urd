# Foundation review: owner outcomes and next step

**2026-11-04 evidence, with subsequent owner-review outcomes marked below.**
Recommendations remain proposals; no production implementation is authorized.

The full README/ARCHITECTURE was reviewed before choosing four investigations.
No Urd implementation, production configuration or scaffold was added. Existing
technology, Core/Server, source-distribution and retained-feature decisions stand.

## What changed our understanding

| Finding | Practical consequence | Evidence and limit |
| --- | --- | --- |
| **Reload can change what the model sees.** Ordinary message JSON changed previously sent literal tool text into quoted text, and native text/image blocks into serialized object text. | Saving IDs and readable messages is insufficient. Qualify exact already-sent results through the eventual context owner; keep `IChatClient`. Typed reconstruction and necessary native-result retention remain alternatives. | [Context evidence](CONTEXT-LIFETIME-EVIDENCE.md): 22 assertions in the final three-request local adapter fixture. The replay control preserved ordered content/values, not identical HTTP bytes. No live model/cache claim. |
| **Loopback and CORS alone do not protect browser requests.** Cross-origin requests carried Strict cookies; cookies also reached another port. A no-CORS-only control still changed server state. | Protect reads, commands and streams with transport-appropriate server-side checks. The owner subsequently chose trusted-local open-and-use, not credential pairing; the fixture's credential controls are not a selected access policy. | [Access evidence](ACCESS-BOUNDARY-EVIDENCE.md): 66 assertions, real headless browser plus server-side controls. Injected synthetic credentials—not onboarding, remote access or production authentication. |
| **Source customization worked without Git.** Extract/build, edit/add/delete backend/UI/guidance, redistribute, extract/rebuild produced the expected visible changes and carried source. | Conventional packaging can support the desired workflow without a self-rebuilding application. Keep ordinary builds separate from distribution checks. | [Source/build evidence](REPO-FOUNDATIONS.md#what-the-round-trip-established): 12 grouped checks. A post-capture edit did not affect staged output/source; copying during edits was **not** qualified. Warm Windows tools/caches, not fresh-machine/offline/other-OS proof. |
| **Durable intent and retained content have different lifetimes.** A due request known not to have started differs from a possibly executed command; a needed shared result differs from its worker's transcript. | Preserve automatic due work without replaying uncertain effects. Worker closure must not delete another open chat's result/attachment—or retain the entire closed transcript to save one result. | [Durability/reference ledger](DURABLE-BOUNDARIES-EVIDENCE.md): source-grounded failure analysis, **no new runtime test**. Includes attachment publication/deletion races and compact final usage. |

## Owner-review outcomes

**1. Setup/custom-copy direction agreed; build baseline partly aligned.** The owner
accepted automatic acquisition of missing project-declared build tools where
practical, with small convenience scripts. Customized copies default to separate
persistent data and keep source, builds and data together in a user-chosen writable
folder. Data survives clean/rebuild and stays outside source/distribution inputs;
exact paths, ordinary installed-release policy, sign-in portability and activation
are not selected. [Recorded answers](REPO-FOUNDATIONS.md#owner-outcome).

The owner aligned with **.NET 10 LTS and Vite**, then said **“Lean toward npm”**
after independent reviews and a concrete comparison. npm is the preferred, revisable
route; Node version and the complete build baseline are not selected.

Revised recommendation: npm with small .NET/Node setup scripts, provided bootstrap
stays simple. pnpm remains a credible alternative if its existing runtime management
concretely reduces maintenance. The earlier pnpm trial remains valid evidence, not
proof of superiority or qualification of npm setup. The three owner-requested
independent reviews were read-only, not new tests.

Automatic tool acquisition still needs qualification. The trial did not compare
different-version copies or a clean machine. Exact pins, physical source format,
SDK/cache shipping and activation remain open. Test strategy remains
fake-model + real-SQLite behavior tests and one built-UI/backend smoke; selecting
routine runners/formatters need not become another owner voting exercise.

**2. Local access direction agreed; first-slice details remain under review.**
The owner selected same-machine browser use first, then LAN, then Tailscale, and
Pie-like open-and-use local access: loopback plus strict, transport-appropriate
browser-request checks. Programs already running on the PC are trusted; no Urd
account, pairing or remembered-browser approval system. This is not individual-client
authentication or permission for arbitrary web origins.

Same-origin hosting and backend-machine provider onboarding remain recommendations.
LAN protection, later Tailscale/application trust and real-device qualification
remain separate work; do not infer unauthenticated LAN or a client-enrollment flow.
A remote browser's provider callback returns to that device's loopback, not the
backend. Provider login is not client authentication to Urd.

The first-checkpoint draft remains two chats/two browser clients with fake-model
streaming, SQLite history, reconnect and settled Stop. Its idle-only Send restriction
was **not approved**; the owner found the staging question unclear. Developer
checkpoint sequencing belongs in concrete engineering planning, not another abstract
owner question. Normal-use steering and follow-up input stay retained. The overall
implementation scope and structure still need alignment before code; neither the
draft nor the experiments approve transport, fine layout, APIs or schema.

## Wider decisions remain visible—not all gates for the first slice

- **Before live context/reset behavior:** resolve remaining model/guidance boundaries
  and capability-selection refresh; preserve readable originals and current permissions.
  Context reset and reselection are different operations. The owner accepted continuing
  an explicit idle provider switch without blocking confirmation solely for nontransferable
  private reasoning, not losing visible content or replaying tools. The owner first
  favored explicit instruction reload, then deferred exact loading/reload behavior to
  the simplest implementation. No per-chat Reload UI, watcher, frozen-forever policy
  or busy-admission rule is approved; ordinary prefix stability, readable history and
  current permission enforcement remain distinct.
- **Uncertain-effect recovery:** the owner delegated “whatever is simplest”, not
  approval of detailed controls. Working recommendation: after old work actually stops,
  retain the missing result as unknown, never auto-replay, and allow a fresh instruction
  without a reconciliation-only lock or wizard. Stop/settlement, stale-write and
  ambiguous-Send-receipt safeguards remain; this is not evidence of the old outcome.
- **Before real retained data:** qualify shared attachment cleanup, coherent bounded
  history reads and summary finalization. Shared storage wording now distinguishes
  provisional output from returned committed database transactions for process crash;
  system crash/power loss is outside that guarantee. No durability upgrade, numerical
  loss budget, file atomicity or exactly-once effect guarantee was introduced.
- **Before normal background use:** offscreen attention, discoverable stop/shutdown,
  credential-store availability and startup-at-login need concrete treatment.
  Independent-worker closure still needs meaningful UI context, not another abstract mock.
- **Later qualification:** delegated capability/fallback fidelity, actual shared UI
  host reuse, rich-history/resource costs, native tools and macOS/Linux releases.
  No live-provider entitlement/cache, portable credential-store, packaged-daemon
  or substantial-agent-capacity claim was established overnight.

## Next step

The evidence/owner-review round is complete. After rejecting the project-first and
`systems/` layouts, the owner requested [source-backed research](STRUCTURE-RESEARCH.md)
and refined the naming and harness nesting. The resulting [application plan](APPLICATION-PLAN.md)
is now accepted as a rough starting structure, explicitly revisable during implementation.
The owner also requests centralized, flat `docs/` with an optional scratch area.
The working layout uses `scripts/` for automation and omits `experiments/`. The
experimental tree has since been [preserved in machine-local scratch](../README.md#experimental-source-archive)
with owner authorization; documentation remains in place. Non-harness internals remain
TBD where not designed. Use this baseline to scope subsequent work rather than
keep refining speculative folders. Existing behavioral/technology decisions stand;
rough-layout acceptance does not itself grant production-code permission.

The four stream reports and shared integration were independently reviewed within
their stated limits. Research recommendations do not turn fixtures into production
architecture. This round added no application code, new runtime tests, live account
use, global setup, push or activation.
