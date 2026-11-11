# Organization audit — 2026-11-11

**Audit only. No application or test changes.** This is a review backlog, not approval to refactor.

## Result

The strongest organizational pressure is in browser presentation and test ownership, not a need to redesign the C# hierarchy. Transcript viewport work spans remote data, DOM measurement and anchoring; large browser suites combine unrelated owners; numeric Markdown wire values rely on manual cross-language synchronization. One concrete stale diagnostic was also found: the transcript baseline requires an obsolete database schema.

**19 findings:** 8 worth reviewing first; 11 smaller cleanup candidates. Priority here means maintenance review order, not production incident severity. Structural concerns are not claims of a demonstrated runtime failure.

## Scope and evidence

- Read **353 first-party files / 52,451 physical lines**: **238 `.cs`**, **82 `.ts`**, **31 `.tsx`**, **2 `.html`**. Every file has an individual assessment in the [coverage ledger](organization-audit-2026-11-11-files.md).
- Included application code, backend/browser tests, helpers, build-entry TypeScript and both HTML entries. Inventory used tracked plus unignored new files; dependencies, ignored generated/build outputs, CSS and other languages were outside the requested file review. Relevant guides/configuration were consulted for context.
- Baseline commit: `bc5b68cbb3a279ac4ab406355cab121cff93262c`. Main frozen working-copy inventory: **2026-11-11 06:33 UTC** (347 files); six newly created compaction-related files were read in supplemental snapshots. The ledger fingerprints identify the exact contents reviewed, including uncommitted edits.
- The initial 345-file read was followed by a full reread of 32 changed/new files, plus the six compaction-related additions. A frozen snapshot prevented later edits from silently changing the evidence.
- Other sessions actively own transcript and context-window/compaction implementation. This report does **not** claim to cover files or edits created after its snapshots. Their unfinished schema/implementation alignment is not recorded as established rot.
- Findings were independently checked against the snapshot and corrected. Verification was static: file coverage, evidence ranges, usage searches and document links; no runtime defect reproduction, tests, builds, live providers or staging. No external scanners/packages were installed.

## Review first

| ID | Finding | Why review it |
| --- | --- | --- |
| [O01](#o01) | VirtualTranscript combines DOM anchor lifecycle with position-window orchestration | DOM anchoring and remote-window orchestration share one component. |
| [O02](#o02) | Workspace retention policy reaches into descendant DOM | The shell decides retention by inspecting descendant DOM. |
| [O03](#o03) | BrowserRegression combines several behavioral owners | Workspace, events, commands and performance scenarios share one suite. |
| [O04](#o04) | Renderer browser ownership is concentrated in one mode-driven harness | Several render/window/lifecycle owners share one mode-driven harness. |
| [O05](#o05) | Markdown enum ordinals are a manually synchronized wire contract | Server enum order can silently change browser meaning. |
| [R01](#r01) | Transcript baseline still requires schema version 11 | The manual baseline aborts on the current schema guard. |
| [T01](#t01) | Session observer tests freeze internal publication counts | Internal notification counts are treated as fixed behavior. |
| [T02](#t02) | Provider settings tests can fall through to the real HTTP client | Partial method overrides leave real transport reachable. |

## Findings

Evidence line numbers refer to the fingerprinted snapshots, not necessarily today’s working tree. Recommendations are bounded options for later review, not instructions to introduce new frameworks.

<a id="o01"></a>
### O01 — VirtualTranscript combines DOM anchor lifecycle with position-window orchestration

**Review first · confidence: medium.**

The same component coordinates asynchronous position-window/model changes and owns DOM measurement, source-anchor capture/restoration, and user-scroll anchoring. Those concerns are tightly coupled at the feature level, but the DOM anchor lifecycle is a distinct responsibility whose changes must currently be reasoned about alongside remote read orchestration. Existing model and navigation-storage abstractions narrow the concern; this is not a claim that the whole transcript feature has the wrong owner.

**Review option:** After the owner-reported WIP settles, consider extracting only the DOM viewport/anchor/measurement lifecycle behind a narrow interface to the existing TranscriptViewport model. Keep window loading and transcript policy orchestration in VirtualTranscript unless another concrete boundary emerges.

**Evidence:**
- [`source/ui/app/VirtualTranscript.tsx:432–603`](../source/ui/app/VirtualTranscript.tsx#L432)
- [`source/ui/app/VirtualTranscript.tsx:850–990`](../source/ui/app/VirtualTranscript.tsx#L850)

<a id="o02"></a>
### O02 — Workspace retention policy reaches into descendant DOM

**Review first · confidence: high.**

Workspace queries document selection and activeElement, walks to the descendant data-chat-session marker, then uses those DOM-derived ids to decide which conversations stay mounted. This makes the application shell depend on a transcript/tab DOM contract for a display-resource policy.

**Review option:** Move pinned-session discovery to the transcript/display owner and pass retained session ids through an explicit boundary, or isolate the existing policy in a named retention hook that owns the DOM contract.

**Evidence:**
- [`source/ui/app/Workspace.tsx:73–94`](../source/ui/app/Workspace.tsx#L73)

<a id="o03"></a>
### O03 — BrowserRegression combines several behavioral owners

**Review first · confidence: high.**

One 2,104-line suite combines workspace/provider UI, event-stream lifecycle, a bespoke push-versus-350ms-poll measurement (including a custom legacy renderer), resend/navigation flows, and transcript geometry. These distinct regression owners make unrelated changes and failures harder to localize; the shared offline browser fixture is genuinely common.

**Review option:** Separate the existing scenarios by behavioral owner, keeping the common browser fixture shared and the benchmark identifiable as measurement code rather than creating a new test framework.

**Evidence:**
- [`source/ui/tests/BrowserRegression.ts:446–862`](../source/ui/tests/BrowserRegression.ts#L446)
- [`source/ui/tests/BrowserRegression.ts:863–1197`](../source/ui/tests/BrowserRegression.ts#L863)
- [`source/ui/tests/BrowserRegression.ts:1198–2104`](../source/ui/tests/BrowserRegression.ts#L1198)

<a id="o04"></a>
### O04 — Renderer browser ownership is concentrated in one mode-driven harness

**Review first · confidence: high.**

A single parent browser test owns independent DOM-semantic, selection, geometry/windowing, message-generation, and link behaviors. Its matching harness also combines scenario data construction with an imperative API for several production owners behind one query-string mode switch. Changes to one behavior can require coordinated edits to central fixture branches and shared controls, making ownership and failures harder to localize.

**Review option:** Keep the shared Chromium/server lifecycle, but move behavior-specific scenario data and assertions behind renderer, window-geometry, and message-view owners; extract only where it removes cross-owner edits.

**Evidence:**
- [`source/ui/tests/MarkdownRenderer.browser.ts:17–45`](../source/ui/tests/MarkdownRenderer.browser.ts#L17)
- [`source/ui/tests/MarkdownRenderer.browser.ts:48–50`](../source/ui/tests/MarkdownRenderer.browser.ts#L48)
- [`source/ui/tests/MarkdownRenderer.browser.ts:406–480`](../source/ui/tests/MarkdownRenderer.browser.ts#L406)
- [`source/ui/tests/MarkdownRenderer.browser.ts:503–549`](../source/ui/tests/MarkdownRenderer.browser.ts#L503)
- [`source/ui/tests/MarkdownRenderer.browser.ts:672–932`](../source/ui/tests/MarkdownRenderer.browser.ts#L672)
- [`source/ui/tests/MarkdownRendererHarness.tsx:114–287`](../source/ui/tests/MarkdownRendererHarness.tsx#L114)
- [`source/ui/tests/MarkdownRendererHarness.tsx:443–518`](../source/ui/tests/MarkdownRendererHarness.tsx#L443)

<a id="o05"></a>
### O05 — Markdown enum ordinals are a manually synchronized wire contract

**Review first · confidence: high.**

The browser decodes numeric enum values by array position while Core serializes its enum ordinals. This is documented and deliberate, but inserting/reordering a backend enum member can silently relabel an otherwise in-range value in the UI; TypeScript compilation cannot detect that drift. The client tests use hand-authored numeric records and the browser harness uses synthetic render trees; the inspected route test checks numeric serialization but not TypeScript ordinal parity. That leaves a specific server-to-renderer drift gap, not a reason for universal runtime validators.

**Review option:** Keep a focused wire-parity test for each enum family (or make the wire values explicitly stable) at this boundary. No generic schema/code-generation layer is warranted by this evidence.

**Evidence:**
- [`source/ui/markdown-rendering/MarkdownClient.ts:26–65`](../source/ui/markdown-rendering/MarkdownClient.ts#L26)
- [`source/ui/markdown-rendering/MarkdownClient.ts:433–468`](../source/ui/markdown-rendering/MarkdownClient.ts#L433)
- [`source/backend/core/markdown-rendering/MarkdownRenderModel.cs:5–13`](../source/backend/core/markdown-rendering/MarkdownRenderModel.cs#L5)
- [`source/backend/core/markdown-rendering/MarkdownRenderJson.cs:5–11`](../source/backend/core/markdown-rendering/MarkdownRenderJson.cs#L5)
- [`source/hosting/server/MarkdownRoutes.cs:260`](../source/hosting/server/MarkdownRoutes.cs#L260)
- [`docs/browser-development.md:60–61`](../docs/browser-development.md#L60)

<a id="r01"></a>
### R01 — Transcript baseline still requires schema version 11

**Review first · confidence: high.**

The manual baseline starts the current server, which initializes schema version 15, then seed() asserts PRAGMA user_version is 11 before loading fixtures. That assertion aborts the qualification before its workload runs, so this diagnostic is stale against the frozen snapshot.

**Review option:** Update the seed contract and version guard for the current schema (or derive the expected version from the schema owner); retain a fail-fast check against stale raw SQL fixtures.

**Evidence:**
- [`source/ui/tests/TranscriptBaseline.ts:118`](../source/ui/tests/TranscriptBaseline.ts#L118)
- [`source/backend/core/storage/DatabaseSchema.cs:7`](../source/backend/core/storage/DatabaseSchema.cs#L7)
- [`source/backend/core/storage/DatabaseSchema.cs:260`](../source/backend/core/storage/DatabaseSchema.cs#L260)

<a id="t01"></a>
### T01 — Session observer tests freeze internal publication counts

**Review first · confidence: high.**

The watched-tool scenario asserts exact versions 8/9, a complete 0..11 sequence, and fixed versions for tool-observed/started/completed. Production increments the version on every Publish call, so harmless additions or rearrangement of publications can break this test while preserving meaningful status/content delivery and monotonic versioning. The observable version contract matters, but these particular internal step counts are not shown to be fixed API guarantees.

**Review option:** Keep assertions for initial status, watched notifications, required state transitions, and monotonic/shared versions; avoid fixing every tool lifecycle publication to a numeric slot unless that exact sequence is an explicit external contract.

**Evidence:**
- [`source/tests/Urd.Backend.Tests/sessions/SessionSubscriptionTests.cs:149–176`](../source/tests/Urd.Backend.Tests/sessions/SessionSubscriptionTests.cs#L149)
- [`source/backend/core/sessions/SessionNotifications.cs:24–35`](../source/backend/core/sessions/SessionNotifications.cs#L24)

<a id="t02"></a>
### T02 — Provider settings tests can fall through to the real HTTP client

**Review first · confidence: high.**

setup() constructs ProviderClient at localhost:1 and shadows only selected methods. A controller operation not overridden by a scenario—or added later—can silently use the real fetch transport, making the test boundary incomplete and potentially introducing accidental I/O.

**Review option:** Use a method-complete test fake behind the narrow provider operations consumed by the controller; avoid adding a generic mocking layer.

**Evidence:**
- [`source/ui/tests/ProviderSettingsController.test.ts:58–71`](../source/ui/tests/ProviderSettingsController.test.ts#L58)

<a id="o06"></a>
### O06 — Initial and reconnection session discovery duplicate pagination

**Smaller cleanup · confidence: high.**

Startup and reconnect hooks each walk SessionClient.list pages, updating the same discovered Set owned by useWorkspaceNavigation. The duplicated mechanism can diverge in cancellation/cursor handling; discovery state itself is shared, not independently duplicated.

**Review option:** If the list protocol changes, extract only a shared paged-session enumerator and leave initial-tab bootstrap and reconnect admission policies at their current owners.

**Evidence:**
- [`source/ui/app/useWorkspaceNavigation.ts:64–88`](../source/ui/app/useWorkspaceNavigation.ts#L64)
- [`source/ui/app/useSessionEvents.ts:39–64`](../source/ui/app/useSessionEvents.ts#L39)

<a id="o07"></a>
### O07 — Send and resend availability policy has split ownership

**Smaller cleanup · confidence: high.**

ConversationState derives canSend and related execution facts, while Conversation separately recomputes an overlapping resend gate from pending, fencing, stop, known-execution, and persistence state. The predicates intentionally differ in some requirements, but their shared restrictions can drift as lifecycle rules evolve.

**Review option:** Consider deriving a separately named canResendNotSent fact alongside canSend in ConversationState, preserving the distinct resend rules rather than forcing both actions through one predicate.

**Evidence:**
- [`source/ui/app/Conversation.tsx:95–107`](../source/ui/app/Conversation.tsx#L95)
- [`source/ui/app/ConversationState.ts:25–69`](../source/ui/app/ConversationState.ts#L25)

<a id="o08"></a>
### O08 — ToolCatalog's name index is unused by invocation

**Smaller cleanup · confidence: high.**

ToolCatalog constructs and exposes an exact-name dictionary lookup, but production invocation resolves names with a linear scan over SessionExecution's frozen tool list; targeted usage search found no production caller of Find. The index and API therefore add per-catalog state without serving the execution path, while presenting a second lookup route to maintain.

**Review option:** Remove the unused name-lookup API/index if no current caller needs it, but preserve its incidental duplicate-name validation explicitly. Do not move invocation off the frozen execution tool set.

**Evidence:**
- [`source/backend/core/tools/runtime/ToolCatalog.cs:6–21`](../source/backend/core/tools/runtime/ToolCatalog.cs#L6)

<a id="o09"></a>
### O09 — Diagnostic and streaming clients duplicate common provider-failure mapping

**Smaller cleanup · confidence: high.**

The two client paths separately map authentication (401), plan access (403), and common transport/provider exceptions to the same safe failure categories. Their additional cases differ (discovery unsupported versus context-window exceeded), but the shared mapping can drift.

**Review option:** If centralizing, extract only the common classification and keep discovery/stream-specific cases local; otherwise retain parity with focused classification tests.

**Evidence:**
- [`source/backend/core/providers/OpenAIProviderClient.cs:191–196`](../source/backend/core/providers/OpenAIProviderClient.cs#L191)
- [`source/backend/core/providers/ProviderModelClient.cs:111–118`](../source/backend/core/providers/ProviderModelClient.cs#L111)

<a id="o10"></a>
### O10 — Workspace harness is a dangling in-tree entry shim

**Smaller cleanup · confidence: high.**

The module only imports base.css and re-exports mountMarkdownWorkspace, but no in-tree UI test, HTML entry, or package script references it; the Markdown browser command instead loads MarkdownRendererHarness.html, while the default app starts main.tsx. Its name can imply workspace-level Markdown coverage that currently is not wired to this shim.

**Review option:** Remove the unused shim if obsolete, or add an explicit test entry that consumes it if full-workspace mounting is still an intended qualification path.

**Evidence:**
- [`source/ui/tests/MarkdownWorkspaceHarness.tsx:1–3`](../source/ui/tests/MarkdownWorkspaceHarness.tsx#L1)
- [`source/ui/tests/MarkdownRendererHarness.html:40–47`](../source/ui/tests/MarkdownRendererHarness.html#L40)
- [`source/ui/package.json:22–23`](../source/ui/package.json#L22)
- [`source/ui/index.html:1–11`](../source/ui/index.html#L1)

<a id="t03"></a>
### T03 — Stored response reader loses run scope and request chronology

**Smaller cleanup · confidence: high.**

Read/ReadText select every output row in the database and order request groups by request_id. Production generates request IDs with Guid.NewGuid() and records chronology separately as request_index, so a multi-request or multi-run caller can receive unrelated text in arbitrary order. Inspected current ReadText call sites use isolated/single-request output, so this is a latent helper-scope defect rather than an identified failing assertion.

**Review option:** Either make the helper explicitly single-request and assert that scope, or accept a run/request identity and order the full-run form by run ordinal, request_index and chunk_index.

**Evidence:**
- [`source/tests/Urd.Backend.Tests/support/StoredLlmResponses.cs:13–14`](../source/tests/Urd.Backend.Tests/support/StoredLlmResponses.cs#L13)
- [`source/tests/Urd.Backend.Tests/support/StoredLlmResponses.cs:20–23`](../source/tests/Urd.Backend.Tests/support/StoredLlmResponses.cs#L20)
- [`source/backend/core/storage/DatabaseSchema.cs:82–93`](../source/backend/core/storage/DatabaseSchema.cs#L82)
- [`source/backend/core/storage/SessionExecutionStorage.cs:12–17`](../source/backend/core/storage/SessionExecutionStorage.cs#L12)

<a id="t04"></a>
### T04 — Two tests are outside their named production owner

**Smaller cleanup · confidence: high.**

The assembler suite's safe-link case calls only safeMarkdownLink, and the handoff suite's UTF-8 locate case calls only SyntheticMarkdownPageSource.locate. The first is hidden under an unrelated suite name; the second verifies test-double behavior rather than a production locator, so it can be mistaken for production boundary coverage.

**Review option:** Place the link-policy assertion with its policy/renderer owner, and remove the synthetic locator self-test or replace it with an assertion against the production client/server locator boundary.

**Evidence:**
- [`source/ui/tests/MarkdownWindowAssembler.test.ts:582–605`](../source/ui/tests/MarkdownWindowAssembler.test.ts#L582)
- [`source/ui/tests/MarkdownGenerationHandoff.test.ts:326–345`](../source/ui/tests/MarkdownGenerationHandoff.test.ts#L326)

<a id="t05"></a>
### T05 — Persisted tool-envelope assertion depends on JSON property order

**Smaller cleanup · confidence: high.**

The test compares enumerated property names as ordered arrays for the persisted output and image reference. The codec persists a record-shaped JSON envelope and reads named fields; object member order is incidental serialization detail, so a harmless record/property reorder can fail despite the same persisted shape and values.

**Review option:** Assert required property presence and values by name (or compare a set of names), while retaining checks for actual envelope fields and restored output.

**Evidence:**
- [`source/tests/Urd.Backend.Tests/execution/ProviderSessionExecutionTests.cs:309–320`](../source/tests/Urd.Backend.Tests/execution/ProviderSessionExecutionTests.cs#L309)
- [`source/backend/core/storage/ToolResultCodec.cs:20–29`](../source/backend/core/storage/ToolResultCodec.cs#L20)

<a id="t06"></a>
### T06 — SSE unwatch test uses a wall-clock sleep for a negative assertion

**Smaller cleanup · confidence: medium.**

After observing the expected change, the test waits an arbitrary 300 ms before asserting that no fourth registration or extra change occurred. This adds fixed runtime and only samples a time window, so delayed work under a slow scheduler can escape the assertion.

**Review option:** Replace the sleep with a test-local registration acknowledgement/drain barrier and assert counts once the controlled update sequence has completed; keep the coordination local to this scenario.

**Evidence:**
- [`source/ui/tests/SessionEvents.test.ts:174–190`](../source/ui/tests/SessionEvents.test.ts#L174)

<a id="t07"></a>
### T07 — TranscriptBaseline duplicates the owned server lifecycle

**Smaller cleanup · confidence: high.**

The baseline has a parallel child spawn/readiness/termination/reap implementation alongside ServerProcess. Its caller-supplied data root and visible PID are legitimate reasons for a distinct composition, but shared lifecycle fixes can diverge.

**Review option:** Either document the intentional lifecycle differences or share only spawn/readiness/reap mechanics while retaining the baseline’s root and PID access.

**Evidence:**
- [`source/ui/tests/TranscriptBaseline.ts:44–111`](../source/ui/tests/TranscriptBaseline.ts#L44)
- [`source/ui/tests/ServerProcess.ts:22–129`](../source/ui/tests/ServerProcess.ts#L22)

<a id="d01"></a>
### D01 — Current-guide inventories have drifted from their code owners

**Smaller cleanup · confidence: high.**

Provider management explicitly names current database schema 10 although DatabaseSchema is 15; browser development describes workspace version 3 although the payload is version 4; the session HTTP inventory omits implemented position/prompt routes. These are current operating/contract guides rather than historical evidence. Duplicated changing facts make system discovery unreliable. The server guide's schema-11 breaking-change milestone is historical, not counted as a stale current-version claim.

**Review option:** Refresh these current-guide facts together and prefer links to the owning schema/state/route definitions where prose does not need a pinned version. Leave dated evidence records unchanged. Context-window/compaction documentation is active WIP and excluded.

**Evidence:**
- [`docs/provider-management.md:12–15`](../docs/provider-management.md#L12)
- [`source/backend/core/storage/DatabaseSchema.cs:5–8`](../source/backend/core/storage/DatabaseSchema.cs#L5)
- [`source/hosting/server/SessionRoutes.cs:34–47`](../source/hosting/server/SessionRoutes.cs#L34)
- [`source/ui/sessions/SessionClient.ts:84–154`](../source/ui/sessions/SessionClient.ts#L84)
- [`docs/server-development.md:69–80`](../docs/server-development.md#L69)
- [`docs/browser-development.md:42–45`](../docs/browser-development.md#L42)
- [`source/ui/app/WorkspaceState.ts:27–31`](../source/ui/app/WorkspaceState.ts#L27)
- [`source/ui/tests/BrowserRegression.ts:770–776`](../source/ui/tests/BrowserRegression.ts#L770)

## Boundaries worth keeping

- Core remains free of ASP.NET; hosting composes routes over Core. Focused storage objects share the database owner instead of growing one giant store implementation.
- Session identity/admission, execution driving, recording and transcript reads have recognizable owners. The accepted small session hierarchy and composed execution path are not themselves smells. No evidence justifies a new project/interface per class.
- Context guidance is separated from runtime assembly; related small data records sharing a file are intentional. Current tool/file/process classes are generally narrow.
- Backend/browser model duplication is normal at a language boundary. The specific Markdown ordinal/parity gap (O05) deserves attention; duplication alone does not justify code generation or runtime validation of every response.

## Candidates deliberately not promoted to findings

- **Tool relevance has no production selector:** explicitly approved metadata-only preparation in `docs/agent-tools-design.md` item 20. Its lack of a consumer is not accidental dead code.
- **Context-window column/schema and new compaction integration:** confirmed active work owned by another session. Snapshot inconsistencies are transient observations, not an audit defect.
- **Markdown view/handoff lifecycle split:** coordination is intricate, but DOM and generation lifetimes are distinct and documented. No supported reason for another lifecycle manager was established.
- **Workspace key `v1` versus payload version `4`:** payload validation already owns compatibility. A stable storage-slot suffix is not evidence of broken versioning.
- **Large scale/correctness fixture only reports timings:** useful diagnostic measurement need not impose a flaky wall-clock threshold. Size or lack of a timing assertion alone does not establish rot.
- **Windows-only process qualification, incomplete planned features, no pre-release migrations:** known scope/policy, not organizational decay.
- **Duplicate-looking SQL/state transitions:** kept where different transaction or lifecycle guarantees justify them. No wholesale repository/service/DTO framework is recommended.

## Suggested review order

1. Requalify the stale baseline (R01) before using it as performance evidence.
2. Review the two UI ownership boundaries (O01/O02) after transcript WIP settles; prefer one narrow seam over a transcript rewrite.
3. Review browser-suite ownership (O03/O04) and the server/browser parity canary (O05) without expanding the test budget.
4. Consider remaining items only when touching their owners. Small duplication is not automatically worth another abstraction.
