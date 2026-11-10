# Transcript engineering — working design

**Status: Stage 1 plan approved; Stage 2 scope, packets and qualification gates
recorded.**
**Updated: 2026-11-10.** Stage 2 package, renderer, virtualizer, persistent projection,
schema and cache-budget choices remain unselected. Implementation is not authorized
by this design record.

## Purpose and authority

Design a rich agent transcript with low memory/CPU overhead, very fast session
switching and interaction, and stable continuous reading through long histories.
Resource use competes with agent work; it is a product requirement, not later polish.
Do not trade correctness or readable history for a superficially small DOM.

Existing decisions in [ARCHITECTURE.md](ARCHITECTURE.md) remain authoritative:
“Responsiveness is a product-wide requirement”, “Keep conversation history linear”,
“Keep original chat history until the session closes”, “Keep tool input/output
inspection on hover”, “Image attachments: files with stored references”, and
“Closed sessions are finished, not a resumable archive”. This work concretizes
those decisions; it does not reopen managed attachments or change retention.

Distinguish throughout:
- **Requirement:** owner direction or existing agreed contract.
- **Proposal:** engineering recommendation still needing scrutiny/evidence.
- **Open:** unresolved product semantics or mechanism.
- **Observed:** source or rendered behavior, not an implementation approval.

## Agreed requirements and workload

- Rich Markdown/code, tables and images; extensible to further structured content
  without treating everything as specially formatted text. Stage 2 core qualifies
  Markdown text/code/tables and entry plus intra-message virtualization. External
  linked Markdown images are a separate fast-follow after core qualification;
  managed attachments and viewers remain Stage 4. Maths and diagrams are deferred;
  check rendering-stack compatibility now and preserve simple extension points,
  without building a general plugin framework or adding their runtime dependencies.
- Long-running sessions mixing hundreds of tool calls, substantial reasoning,
  normal assistant output, large code blocks and large tool results. These are
  representative workloads, not hard caps.
- Pursue backend-derived reusable formatting structure with browser-owned rendering
  and layout. Original source stays authoritative; derived structure is disposable.
  Owner approved this responsibility split, subject to parser/streaming feasibility.
  No package, wire format, new schema or server-generated HTML follows from it.
- Virtualization is expected from the start. Fetch older sections on demand while
  scrolling; a slight loading delay is acceptable. Do not load the whole history
  just to open a tab or visit an old section.
- The scrollbar represents the **whole retained conversation**, not only the loaded
  range. Dragging should allow distant jumps without progressively fetching every
  intervening page. Offscreen height estimation needs deliberate engineering.
- Future user-prompt markers/buttons along the scrollbar are desired direction,
  not an immediate implementation requirement. Their positions must correspond
  approximately to prompts' actual positions in the full transcript. Navigation
  and markers must not develop incompatible position models.
- Full-history search is deferred. Native browser Find may be limited to rendered
  content for now; do not load hidden history to make it exhaustive. First-party
  search can be added later. No search index or search-specific API is in scope.
- Long replies remain in the **continuous formatted conversation**. Diverting
  oversized replies to a separate paged plain-text reader is not acceptable.
  When a large streamed reply outpaces formatting, keep the latest consistent
  formatted view and catch up in formatted updates, rather than flash raw Markdown.
  This must not block execution, input or scrolling. No fixed refresh interval or
  permission for unbounded display lag follows.
- Reading position and following intent should survive browser reload/restart.
  Retain small local navigation state, not full transcript content; no cross-client
  navigation synchronization is required by this decision.
- Inactive tabs may evict transcript content and briefly reload their reading
  window when revisited. Preserve the reading anchor and following intent; keeping
  every open tab warm is not required. Owner suggested predictive warming on hover
  as a general interaction pattern, not something restricted to tabs.
- Use the simplest selection approach: native browser selection with temporary
  retention of the selected rendered span, released when selection clears rather
  than on pointer release. Keep unrelated content virtualized. Owner rejected
  premature selection restrictions and directed simplicity after reviewing options.
  No custom selection engine or separate prototype phase is planned unless a
  concrete implementation problem justifies reconsideration. Temporary memory
  growth is a trade-off to test; no arbitrary selection cap has been approved.
- Session switching, input, scrolling and animations must be exceptionally
  responsive and lightweight. Preserve reading position, focus and drafts; no
  unintended jumps. Animation must not disguise layout instability.
- Images may be sparse but are first-class. User images are likely more frequent
  than output images; clipboard screenshots for UI work are an important input.
- Uploaded/tool-produced attachments follow the managed-file direction: stable
  identities and metadata, with bytes independent of original paths. Necessary
  references from other sessions/results must survive an originating consumer's
  removal according to existing retention rules.
- Markdown-linked pictures remain **external**; viewing them does not automatically
  retain a managed copy. The owner accepted this as the simplest policy. A separate
  save-picture feature is not required initially. Owner approved automatic lazy
  loading as external images approach the viewport, including requests to their
  hosts. This is not background fetching of all history or managed-copy retention.
  Implement this as a separate fast-follow after Stage 2 core qualification, not as
  part of the core text/code/table gate; managed attachments and viewers remain Stage 4.
- Provider-supplied formatted reasoning summaries appear inline, visually muted
  but readable. Unsummarized reasoning exposed by a provider is collapsed and
  inspectable. Opaque/encrypted continuation state is not public transcript content.
- A user submission accepted while the model is busy is pending input, not yet a
  normal user entry in the conversation. Owner wants it placed as a user message
  when Urd starts sending it in a model request, rather than waiting for a response.
  If the connection/request then fails, it stays in the conversation with that
  failed attempt; do not return it to the pending queue automatically. This changes
  current admission-ordered display semantics; durable acceptance remains separate.
- Tool input must stream into the transcript as the model constructs the call,
  before execution. Providers that do not expose incremental input must not get
  fabricated streaming. Drafting a call is not authorization to execute it.
- Tools use compact rows, generally one per call, with on-demand deeper inspection.
  Tool icons also expose rich hover inspection. Owner clarified that its default
  content should reflect **what the model produces and is shown**, including
  tool-result pruning/reduction, rather than raw execution I/O. Interactive details
  remain reachable for selection/copy, keyboard and click, not fragile hover-only
  text. Their data/rendering lifetime belongs in this design. Pre-pruning omission,
  recall instructions and retained source material are the pruner/producer's
  responsibility, not a transcript/UI policy. A pruning system is outside this work.
- Pie is a behavioral/visual reference, not an architecture, code or stylesheet to
  inherit. Do not spend this phase debating appearance. Current theme direction is
  flat dark surfaces, restrained separation, no shadows/bright popup surfaces,
  while retaining visible keyboard focus.

## Current implementation — observed constraints

Source spot-check at `6519de9` on 2026-11-08; recheck before implementation.

- `source/ui/app/Transcript.tsx` displays escaped text. IntersectionObserver gates
  content reads, with up to four pages per read. Already fetched bodies remain in
  row state; visibility gating is not a bounded source cache or DOM virtualization.
  Selection pauses updates; ResizeObserver anchors a message row. Row-top anchoring
  alone cannot protect reading inside a large message that reflows above the reader.
- Workspace mounts the active transcript keyed by session and retains reading
  positions. Preserve draft/admission behavior while changing transcript ownership.
- `source/ui/sessions/Messages.ts` and backend `core/sessions/Messages.cs` expose
  user/assistant text only, with identity, history revision, cursors and lifecycle
  facts. No public content-part, tool-inspection, attachment or reasoning projection.
- `SnapshotReader.cs`: at most 60 recent messages and 256-UTF-16-unit previews.
  This is not an older-history navigation API.
- `MessageContentReader.cs` / `StorageText.cs`: pages are at most 16 KiB UTF-8,
  examining at most 16 segments. Cursors are segment-local scalar-safe byte offsets,
  not JavaScript character positions. `hasMore=false` means the committed tail,
  not completion. Closed message, final text marker and execution settlement differ.
- User text is saved in `input_decisions.original_text_utf8`; assistant text in
  immutable `execution_output.text_utf8` segments. Current `UserMessageStorage.cs`
  inserts a user history-index row at admission, before `ClaimNextRequest` assigns
  it to a model request. This conflicts with the newly requested display promotion
  boundary; change the projection/order contract without weakening durable receipts.
  Submission text is limited to
  16 KiB UTF-8. There is no implemented image upload/storage/delivery surface.
- Tools are persisted separately in `execution_tools`; native continuation is in
  `execution_provider_context`. `LlmResponseAccumulator.cs` excludes reasoning from
  public text. Do not expose the native envelope as a shortcut to reasoning UI.
- `ProviderStreamState.cs` withholds executable calls until validated completion.
  Draft tool streaming requires an additional observation contract that preserves
  execution safeguards. Normal provider validation rejects image content; opaque
  same-scope native-codec replay tests are not image input/output feature support.
- `/api` requires `X-Urd-Client: browser`. An image element cannot supply that marker.
  Future binary delivery needs an explicit design, not accidentally weakening the
  browser gate or inventing attachment URLs in frontend types.
- Existing model-context reconstruction can read much more than the bounded public
  transcript. Keep transcript measurements separate from model-context costs while
  testing their competition for backend resources; UI work is not permission to
  redesign compaction or execution.

## Proposed engineering model — not yet selected

| Layer | Contents | Ownership / release question |
| --- | --- | --- |
| Authoritative disk history | Original text, tool facts, references; managed image files | Existing retention and mutation rules; no deletion merely because offscreen |
| History index / descriptors | Stable identity/order, kind, availability and small previews | Efficient indexed range reads; avoid materializing every row in browser memory |
| Browser working set | Content near viewport and explicitly inspected details | Bound retained bytes/work, not only message count |
| Warm cache | Recently useful content/layout and session reading positions | Bound across sessions; release under eviction, not keep every tab mounted |
| Rendered resources | DOM, parsed structures, highlighting, decoded images | Keep near viewport or while needed by selection/focus/inspection |

“Cold” means saved content not resident in the browser, not a selected second
storage system. These layers describe responsibilities, not six new services or
mandatory duplicate representations. Avoid retaining original strings, copies,
multiple syntax trees, highlighted output and DOM for the same entire history.

### Retrieval and identity

Design operations for latest-window reads, restoration around a stable reading
anchor, preceding/following ranges, sections within a large item and independent
inspection/media reads. They must not require full-history reconstruction.

Resolve stable ordering across user messages, reasoning, assistant text and tools,
including parallel tool calls. Owner clarified that pending user input is treated
separately until it enters the model exchange, rather than appearing immediately
among conversation messages. Current admission order and model-delivery order are
different facts; retain admission evidence but do not equate it with display order.
Derive display blocks without making generated HTML the source of truth. Define
revision invalidation for edits and how a removed anchor falls back.

Input promotion is agreed at the product level: once Urd starts sending a request
containing the input, promote it without waiting for a response. If that attempt
fails, keep the message with the failure rather than returning it to the queue.
No automatic resubmission follows. “The model read it” cannot be guaranteed from
local dispatch alone. Current `ClaimNextRequest` durably assigns pending inputs and
reconstructs context before provider enumeration; the owner has not selected that
internal transaction as the sending boundary. Define explicit lifecycle/read facts,
not a UI guess based on receipt timing or first assistant text. Pending-input
cancellation/recovery presentation and historical-data compatibility remain open.

Read-only navigation feasibility check (2026-11-08):
- `execution_messages` already has global `ordinal`, unique execution/role/message
  identity and a `(session_id, history_revision, ordinal DESC)` index. New anchor
  and adjacent-window APIs can use it without loading bodies.
- Global ordinals are **not per-session ranks**. Gaps from other sessions/revisions
  make ordinal interpolation unsuitable for whole-history positioning.
- Current APIs expose neither total count/rank nor user-prompt marker positions.
  Counting/filtering can use existing SQL but repeated whole-history scans are not
  a cheap distant-position lookup. No aggregate height/size index exists today.
- Assistant segment count is available from request metadata. Total message bytes
  would need aggregation over output segments; counts alone are poor height
  estimates. Tools/reasoning are not currently history-index rows.
- Storage shares a connection lock; repeated aggregate scans can compete with
  execution writes. Streaming appends do not change history revision, so revision
  alone cannot invalidate live-tail geometry. No current production edit operation
  increments that revision; future removed-anchor handling is still design work.

### Streaming and reconciliation

Backend progress must remain independent of display. Hidden sessions or a reader
far above the live tail should not parse/render every arriving body update.
Coalesce obsolete display updates without dropping authoritative content.

Model-facing tool inspection must preserve the historical result representation
supplied for that exchange, not rerun today's reducer when an old row is opened.
Later context compaction must not rewrite the original readable exchange. This is
an engineering implication of the clarified display direction and existing readable
history contract, not a decision to archive all provider-native/private context.
Distinguish preparing a model-facing result from actually submitting it in a request;
UI availability alone is not evidence that a model consumed it.

Keep partial tool input distinct from validated saved calls. Correlate draft and
final identity; preserve positive provider-terminal and cleanup requirements for
execution. Incomplete argument text need not be valid JSON. Decide what draft
state a new observer needs without replaying an unlimited event log. Existing
[practical crash recovery](ARCHITECTURE.md#practical-crash-recovery-coherent-continuation-not-exact-loss-budgets)
explicitly permits losing an unfinished tool draft on backend crash. Do not reopen
that decision or introduce per-token durability. Browser reconnect to a still-running
backend is a separate synchronization problem; obtain current provisional state
without mistaking it for committed history. Stage 1 adds push change notices
(decision 16); draft-state delivery for stage 3 reuses that channel, not a new one.

Reasoning needs explicit provider provenance/content semantics. Prefer default
collapse when classification is unknown; do not infer summaries from model names
or prose appearance. This is a proposal needing provider capability investigation.

### Eviction, restoration and predictive warming

Owner approved inactive-tab content eviction. Preserve small navigation state;
retain a bounded shared warm cache rather than making memory grow with open tabs.
Cold restoration targets the saved reading window directly. Owner approved saving
small local anchors/following intent across browser reload/restart, independently of
content caching. Current Workspace positions are in memory, so this requires a
persistence change. Storage format, save cadence and removed-anchor fallback remain
engineering questions; do not write full navigation state on every scroll frame.

Owner suggested warming on hover, applicable to other on-demand interactions too.
Proposed mechanism, not selected implementation: treat hover/focus as a hint to
prefetch a small internal read, not permission to mount expensive views, decode
original images or start execution. Share a bounded speculative-work budget across
features; prioritize actual navigation, deduplicate overlapping reads, cancel or
ignore obsolete work and prevent rapid pointer movement from retaining many bodies.
Speculative work should not worsen streaming/input latency. Its failure does not
need an unsolicited error; an actual requested read must report failure truthfully.
This is not permission to fetch external image URLs on hover, call a model/provider,
or perform command side effects. External images instead follow the separately
approved near-viewport automatic-loading policy, not tab-hover prefetch.

### Viewport and layout

Virtualize within large replies as well as between rows. Preserve a visible
item/block plus its offset. Estimate and correct offscreen geometry without moving
content under the reader. Keep user following intent separate from scroll events
caused by our own compensation. Respect reduced motion.

Whole-history scrollbar semantics are agreed; the mechanism remains proposed:
use a shared position model for scrollbar navigation and future prompt markers.
Stable item/block identities locate content; estimated and measured heights project
those locations into pixels. Do not persist pixel coordinates as prompt identity.
Investigate aggregate range estimates and indexed target lookup so a distant jump
need not download one descriptor per message or scan the whole history on each
navigation. This does not yet select an index schema or a particular tree structure.

A marker's displayed position may be approximate, but activating it must resolve
its actual prompt. Height correction must preserve the visible content anchor.
Scrollbar-thumb stability during dragging and corrections after jumps need explicit
behavior and testing, not a claim that all offscreen heights can be known exactly.

Measurements depend on width, typography, content revision and expansion state.
Old measurements cannot be reused blindly after resize/zoom. Image dimensions
should be available before decode where possible. Define selection/focus behavior
when their content would otherwise be evicted, without allowing an unbounded pin.

### Selection and virtualization — feasibility branch

Owner challenged the proposed local-selection-only shortcut. Temporary retention
is a valid candidate, not a reason to abandon normal selection. Retaining source
text alone does not preserve native browser selection/copy when selected DOM is
removed or replaced. Selection lifetime extends beyond pointer release, until the
selection is cleared/replaced; streaming rerenders and keyboard extension matter too.

Two approaches, supported by public documentation inspected on 2026-11-08:
- **Keep selected content mounted:** expand the render range to include the selected
  span while continuing normal virtualization elsewhere. TanStack Virtual's
  [rangeExtractor](https://tanstack.com/virtual/latest/docs/api/virtualizer#rangeextractor)
  permits rendering indexes outside the visible range. That is a mechanism for
  retention, not a documented guarantee of correct selection. Within-message
  virtualization must also retain relevant blocks; preserving endpoint rows alone
  does not preserve omitted text between them.
- **Represent selection independently of DOM:** CodeMirror 6 stores selection as
  document positions in [editor state](https://codemirror.net/docs/guide/#selection)
  while [rendering only a viewport](https://codemirror.net/docs/guide/#viewport).
  Its document model supports extraction beyond rendered text. This is an editor
  architecture, not a drop-in solution for a rich transcript: it owns a resident
  document and handles selection/clipboard behavior. Urd would additionally need
  lazy content retrieval and source-to-rendered text mapping. Do not claim this
  example bounds source memory or establishes rich-clipboard fidelity.

Owner directed adopting the simplest approach. Design direction: native browser
selection with selected-span retention, while keeping content identities independent
of DOM. Do not disable all virtualization or decode unrelated offscreen images
merely because selection exists. An arbitrarily large selected span can still
exhaust memory; temporary growth is a trade-off to qualify, not a bounded-memory
proof. No silent truncation, disappearing selection or arbitrary cap is approved.
Do not build a custom logical-selection engine speculatively.

Test during implementation: cross-row/within-message selection, copy completeness
after scrolling, pointer release, keyboard extension, streaming updates, reverse
selection and memory release after deselection. No separate prototype phase is
planned absent a concrete blocker. Public documentation is not runtime proof.
Whole-item copy actions remain complementary, not a substitute for normal selection.
Clipboard operations themselves may need transient full payload memory.

### Markdown and large payloads

**Table dialect decision:** use standard GFM table semantics. Within a table cell,
unescaped pipes inside code spans still delimit cells; escaped pipes remain cell
content. Markdig's permissive pipe-table behavior is not the selected dialect. This
resolves the dialect choice only: it does not qualify that behavior in a production
parser or select a parser/package.

Transport pages are not independent Markdown documents. Fences, lists, tables and
reference definitions can cross boundaries; later content may change earlier
interpretation. Investigate parsing correctness, work scheduling and retained
structures independently of DOM virtualization. One huge code block/table also
needs attention; splitting only at top-level Markdown blocks is insufficient.

Use proven parser/rendering components rather than a homemade Markdown parser.
Owner approved treating math/diagrams as optional: plan compatibility now and defer
feature implementation. Preserve source; choose a
parser with extension points; allow content-specific renderers to share lazy loading,
measurement and cleanup behavior. Labelled code fences can supply future diagrams;
inline math needs parser-level support and cannot be treated solely as a block
component swap. Keep generated display/parse caches disposable rather than making
an initial renderer's HTML the durable format. Adding syntax can require reparsing
and invalidating derived layout; do not promise zero future migration work.

This is not a proposal for user-installable renderer plugins, an abstract rendering
framework or eager math/diagram dependencies. Check candidate stack compatibility
before selection; defer detailed feature design and runtime qualification until
needed. Streaming, selection, scroll stability and resource release still apply to
future renderers, so extensibility alone does not establish their performance.
No package is selected. Earlier discussion suggested a unified/mdast/hast pipeline,
but this was not adopted or qualified; compare alternatives after defining needs.
Do not assume workers are automatically better: transferring/copying content and
maintaining extra trees can increase memory. Benchmark before adding them.

### Cold access within one huge message — agreed ownership direction

A correctly formatted middle slice must match the whole message's interpretation,
not an independently parsed page. A fixed overlap is insufficient: a reference
link/image can be defined arbitrarily far away; fences and nested lists carry
state; a later table delimiter, setext heading underline or blank-separated list
item can alter the preceding block. A huge paragraph can contain inline constructs
crossing any page boundary. Streaming interpretation is therefore provisional.

Two ownership options were examined through read-only design analysis:
- **Backend-derived structure/index:** resolve Markdown semantics and serve
  addressable display structure/source ranges. Browser reads need not acquire the
  whole message or AST; reusable derived data avoids repeated cold reconstruction.
  Adds projection/invalidation work and moves parsing cost to the backend—it does
  not eliminate that cost or establish bounded backend memory.
- **Browser-owned parsing/index:** simpler source-serving backend, but a cold jump
  may need substantial or complete source scanning before the slice is correct.
  A worker alone does not bound source/AST memory; eviction can force repeat work.

Owner approved pursuing this split: backend owns reusable, disposable semantic
structure for large-message retrieval; browser owns components, styling, measured
geometry and viewport resources. Source remains authoritative. This is not backend
HTML generation, a new parser, a selected AST wire format or an instruction to
process every message eagerly. Keep parsing outside database/session critical
sections; schedule it so transcript work does not stall execution or input.

Needed information, not a selected schema: source identity/ranges and committed
extent; dialect/parser generation; resolved structural/inline context; effective
reference definitions; addressable pieces inside giant blocks; and consistent
layout policy for shared structures such as table columns. History revision alone
cannot identify a growing tail. Derived state must be invalidatable/rebuildable,
with publication tied to the source extent it actually represents.

Unqualified risks remain: parser support for reuse while streaming; initial parse
cost for a cold unindexed giant message; sub-block slicing without copying a whole
AST; source-to-rendered anchoring; extreme inline constructs/table cells; and
content whose later bytes change earlier interpretation. No constant-size context,
instant cold access, fixed memory ceiling or exact final live formatting is promised.
No parser, schema, transport or implementation has been selected by this direction.

### Derived-state lifecycle and reads — proposed mechanics

Keep one reusable structural derivation per relevant source/dialect version, not a
new tree per browser tab. Store compact, addressable derived records alongside
existing session storage if measurement supports it; do not serialize a library's
live AST as the canonical history. Exact storage shape remains unselected.

1. Save authoritative output independently of transcript viewers. Committed source
   and provisional tool drafts retain their existing different durability meanings.
2. A visible read requests missing/stale structure for a known committed extent.
   Coalesce repeated requests and prioritize actual reading over speculative warming.
   Do not format every hidden session on every stream update.
3. Read the required source consistently, then parse/project outside database and
   session locks. Bound concurrent work and pending jobs across sessions. The basic
   whole-message-parser candidate still needs a whole-message temporary allocation;
   queue control does not remove that per-job cost.
4. Publish a projection tagged with the extent it represents. A later append need
   not invalidate that useful prefix result: show it as behind the current source
   and schedule the latest extent. Do not discard every result during sustained
   streaming and starve the display. History replacement/incompatible dialect change
   is different and must reject an obsolete projection.
5. Fetch descriptors and display slices from a consistent derivation generation;
   do not combine offsets/structure from one parse with another's content. Use
   source-based locations rather than transient AST node numbers for saved anchors.
6. Release parser trees and backing strings after extraction. Retain only deliberately
   reusable derived data. Browser caches independently release fetched source,
   component state, highlight results, decoded images and measurements as applicable.

Logical reads—not selected endpoint/type names—are: recent window; window around
an anchor; older/newer neighbors; approximate whole-history position lookup;
subranges within a large item; and independent tool/attachment detail. Each returns
bounded useful data plus continuation/availability facts. Missing derivation means
explicit pending/loading, not an empty item or falsely completed transcript.
No search index or full-history descriptor download follows from these reads.

The backend's structural estimates and browser's measured heights must refer to the
same source locations. Width-dependent pixel geometry remains browser-owned; future
prompt markers reuse that mapping. Formatting corrections preserve the reader's
source anchor and active selection rather than rerendering the entire viewport.
These mechanics remain design proposals until the parser/projection path is qualified.

### Parser candidate evidence — not a dependency selection

Read-only upstream inspection on 2026-11-08 examined Markdig as a natural .NET
candidate at [`ac4f83e79d3829e7d181ec360e07afc3101d4dd0`](https://github.com/xoofx/markdig/tree/ac4f83e79d3829e7d181ec360e07afc3101d4dd0/src/Markdig).
That source inspection installed no package and ran no tests; current-main feature
availability in a release was not inferred. The later approved isolated probe used
released Markdig 1.6.0, as recorded below, not an unpinned main build.
- `Markdown.cs` / `Parsers/MarkdownParser.cs`: inspected public parse entry points
  accept a whole .NET string, build a new document, then process inlines. No previous
  tree/append/resume API was found in those surfaces. Internal pooling is not
  incremental parsing. This does not establish what a custom parser fork could do;
  building one is not proposed.
- `Syntax/SourceSpan.cs` / `Syntax/MarkdownObject.cs`: spans use inclusive ends and
  UTF-16 string offsets; precise inline locations require enabling that option.
  These cannot be copied directly into Urd's UTF-8 byte cursors.
- `Helpers/StringSlice.cs` / `Syntax/LeafBlock.cs`: nodes can retain slices backed by
  larger strings. Keeping a few AST nodes can pin whole source buffers. A bounded
  projection must not accidentally keep the parser tree/backing source resident.
- Fenced-code lines and table rows/cells can be traversed without generating full
  HTML. That supports custom structural extraction, not lazy/bounded parsing.
- [Custom renderers](https://xoofx.github.io/markdig/docs/advanced/renderers/) and
  parser extensions support non-HTML output and future math handling. Check
  [table options](https://xoofx.github.io/markdig/docs/extensions/tables/) explicitly:
  the probe's permissive pipe-table behavior differs from the selected standard GFM
  dialect and is not itself the chosen behavior.

Implication: this candidate supports the chosen ownership split structurally, but
whole-message parse cost and streaming reuse remain the qualification gate. Do not
reparse on every token, retain its AST as the permanent index, or claim that moving
parsing to the backend bounded total process memory. The simplest candidate strategy
is demand-driven, coalesced whole-message parsing with disposable ASTs; measure it
before introducing custom incremental machinery or selecting the dependency.

### Focused parser probe — measured scope

The approved isolated Markdig 1.6.0 probe passed its 41 result records: source-span,
reference and slice checks; three parses for each of four content shapes at three
sizes; and four streamed-growth schedules. See
[parser evidence](TRANSCRIPT-PARSER-EVIDENCE.md) for method, raw-evidence location,
measurements and limitations. Independent final static review checked the raw
records, recomputed summaries and approved the claims/limits without findings;
it did not independently rerun the fixture.

A roughly 2 MiB table took 343–514 ms per parse and about 57 MiB extra live managed
memory with the AST held. For the same growing mixed message, 64 whole-prefix parses
allocated about seven times as many bytes as eight parses. Detached middle slices
were small and correct for the fixtures; this does not qualify a production range
index, general rich-cell rendering or browser interactions.

Keep demand-driven reuse and bounded shared scheduling. Do not equate cancelling a
queued parse with stopping an in-flight synchronous parse. No dependency selection,
custom incremental parser or numeric product limits follow from these results.

Owner approved keeping the latest consistent formatted view and catching up in
formatted updates when a very large active message outpaces parsing. Do not flash
raw Markdown as an intermediate fallback. “Formatted chunks” means coherent display
updates derived with the necessary document context, not independently parsing
transport byte pages. Previously displayed formatting can still change as new
Markdown resolves earlier syntax; preserve the reader's anchor and selection.
A generating indicator acknowledges ongoing work independently; input, scrolling
and execution remain responsive. Refresh cadence and acceptable lag need measured
qualification, not an arbitrary interval or indefinite starvation.

### On-demand details and images

A compact tool descriptor must not carry full input/output in every history update.
Opening inspection requests relevant content; huge payloads and live tails need
bounded reads/rendering. Closing ends unnecessary subscription/rendering work;
cache retention, if useful, must be explicit.

Owner clarified the inspector's primary representation: **model-facing**, not raw
execution I/O. For example, pruning may remove needless whitespace/formatting or
omit huge output to protect the model context. Show the resulting tool response,
not an unrelated full process log or an additional UI-generated summary pretending
to be what the model was shown. Input corresponds to what the model produces.
The transcript is not a dump of hidden instructions or private native continuation.

Keep these boundaries distinct: underlying process/service output; the tool's
returned result; and its post-processing/model-facing representation. Owner assigns
omission/recall policy to the **pruner/producer**, not the transcript. For enormous
output, that component may substitute an omission note and instructions/reference
for retrieving the material. This work does not implement a pruner or select its
thresholds, raw-result retention policy or recall mechanism.

Transcript responsibilities stop at faithfully retaining/projecting the supplied
model-facing content and its explicit references, then loading/rendering on demand.
Viewport eviction must not alter the model-facing result; the UI must not invent
omission notes, independently prune content or become a second raw-output archive.
Any retained references still follow the existing shared-reference lifetime contract;
separating responsibilities does not make a dangling recall link acceptable. The
exact pruner/storage ownership interface belongs to that later feature.

[Context lifetime evidence](CONTEXT-LIFETIME-EVIDENCE.md) distinguishes raw execution
results from model-facing results; [storage research](STORAGE-RESEARCH.md) leaves
capture fidelity open. Earlier proposals to make original tool results the default
inspector content, or settle global pre-pruning retention in this UI discussion,
are superseded by the owner's clarified boundary.

Separate original files, generated previews, decoded previews, full-resolution
viewer resources and pending clipboard/upload data. Small display size does not
mean small decode cost. Define dimensions, format validation, preview generation,
object URL lifetime, cancellation and cache ownership. Respect existing managed
attachment publication/reference rules; no general artifact policy is selected.

## Grill queue — resolve dependencies, one scenario at a time

Give a recommendation with each product question. Answer code questions by reading
source, not by asking the owner. Update this document immediately after decisions.

1. **Navigation semantics — resolved at product level:** whole-conversation scrollbar
   and arbitrary distant jumps; engineer height estimates with future user-prompt
   markers in mind. Open mechanisms: range summaries, target lookup, width-dependent
   measurements, drag stability and correction without visible content jumps.
2. **Return to session — product behavior resolved:** owner permits inactive content
   eviction, suggests predictive warming, and requires reading/following state to
   survive browser reload/restart. No permanent hidden render trees or full persisted
   content cache follows. Numeric targets, eviction/prefetch budgets and local
   persistence details remain unselected.
3. **Content ownership/identity/order:** model-facing inspection, pruner ownership
   and user-input promotion semantics are resolved. Promote when sending a containing
   request starts; keep a failed attempt in history, not automatically back in the
   queue. Current admission-index projection must change. Relationships, stable
   draft-to-final identity, revision invalidation and pending cancellation remain
   mechanism/product questions as applicable; do not reopen existing Stop guarantees.
4. **Streaming recovery:** existing architecture permits loss of an unfinished tool
   draft on backend crash. Preserve that simple contract; distinguish browser
   reconnect/current provisional state from committed history and terminal facts.
5. **Large-item correctness — current engineering branch:** optional math/diagram
   compatibility now and implementation later is approved; no general plugin
   framework. Use the recorded standard GFM table dialect; determine remaining
   Markdown semantics, code/table virtualization, streaming parse strategy and
   memory ownership before choosing dependencies. In particular, arbitrary transport
   pages cannot be parsed as independent Markdown documents.
   Owner approved backend reusable semantic structure with browser layout/rendering.
   Next check existing parser capabilities against the split, especially streaming
   reuse and sub-block ranges, before selecting a package or projection format.
6. **Interaction through eviction — current branch:** full-history search is
   explicitly deferred; owner accepts browser Find's limitation for now and may add
   first-party search later. Do not design a search index/API in this phase. Owner
   chose simplicity for selection: native selection with temporary selected-span
   retention, verified during implementation rather than a separate prototype phase.
   Custom selection is not planned without a demonstrated need. Also resolve
   keyboard navigation, expanded details and resize.
7. **On-demand payloads/media:** automatic near-viewport loading of external Markdown
   images is approved; URLs stay external and no managed copies are created.
   Also cover inspector lifecycle, large model-facing results, clipboard uploads,
   attachment publication/cleanup and image preview/viewer release. Display rendering
   does not imply provider image delivery.
8. **Budgets and qualification:** representative workload sizes, numeric latency
   and resource targets, cache/overscan policies, contention and failure cases.
9. **Implementation sequencing:** bounded milestones and ownership only after
   the above contracts and high-risk feasibility checks are coherent.

## Proposed implementation sequence — not yet approved

Owner approved making stage 1 concrete as a **plan**, not implementing it. See
[History/navigation plan](TRANSCRIPT-HISTORY-PLAN.md) for source-grounded contracts,
read/placement packets, migration policy and acceptance gates. It explicitly keeps
message-rank navigation distinct from stage 2's estimated rendered geometry.

These are bounded delivery slices, not authorization to implement the entire design.
Before the first source edits, settle its concrete read contract and ownership,
coordinate active Workspace/CSS/storage work, and choose only the dependencies that
slice requires. Markdig remains a candidate; the parser probe does not select a
production parser, projection format or a browser virtualizer.

1. **History, live-update and placement foundation.** Kind-tagged entry index
   (user, text run, tool) in observed order, with entry times, tool start/end and
   per-reply usage; metadata-only latest/anchor/neighbor reads; one multiplexed push
   stream per browser page replacing snapshot polling; conversation placement at
   send-attempt start, preserving durable receipts and failed-attempt history. Whole-history
   position lookup is deferred to Stage 2. Verify revision fencing, mixed kinds,
   notice coalescing and query cost without reading bodies. Do not weaken existing
   delivery/Stop guarantees.
2. **Stage 2 core — formatted, virtualized text.** Qualify Markdown text, code and
   tables; virtualize between entries and within large messages/blocks. Preserve the
   approved backend semantic-structure/browser-rendering split, with formatted catch-up
   and no raw-Markdown flash. External linked Markdown images are a separate
   fast-follow after core qualification. Managed attachments and viewers remain
   Stage 4. Deliver through the small reviewable packets and gates below; this is not
   a package, projection-format or implementation approval.
3. **Typed tool/reasoning content.** Add provisional call observation and snapshot
   recovery without weakening terminal validation or execution authority. Project
   model-facing input/results, statuses and explicit reasoning categories. Fetch
   inspection data only when needed and release it when no longer useful. Verify
   draft-to-final identity, failure/cancellation, offscreen streaming and interrupted
   observation. No pruner, raw-log archive or opaque-continuation exposure.
4. **Managed images and clipboard input.** Complete storage/publication/reference
   ownership, metadata/previews, gated binary delivery, upload/draft lifecycle and
   bounded viewer resources. Provider image input/output is its own capability path,
   not proof obtained from rendering a picture. This slice may need broader execution
   and model-context changes; bound and approve those before starting rather than
   treating the feature as a transcript-only frontend patch.
5. **Later cross-stage qualification and tuning.** Repeated long-session navigation,
   concurrent streaming, many tool calls and screenshots under realistic resource
   contention. Measure both browser and backend, warm and cold paths, persistent index
   cost and post-eviction memory. Derive concrete cache/concurrency/latency targets
   from this evidence with the owner; synthetic parser sizes and safeguards are not limits.

### Stage 2 packets and acceptance gates

These are small, reviewable packets with non-overlapping questions, not authorization
for a broad rewrite. Owning code areas are named to guide later coordination; no
speculative API, schema or class design is implied.

1. **Decision probes** — The bounded Stage 2 decision probe and targeted fidelity
   follow-up are recorded in [parser evidence](TRANSCRIPT-PARSER-EVIDENCE.md), with
   raw reports at `%TEMP%/urd-stage2-decisions-20261110-4f84ef2a/REPORT.md` and
   `%TEMP%/urd-stage2-fidelity-20261110-250eb606/FOLLOWUP-REPORT.md`. The follow-up
   records 6/6 bounded GFM table cases, 132/132 browser assertions and 2/2 native
   selection gestures retaining selection after visible append-only updates. This
   supports an initial Markdig semantic-derivation implementation only; it is not
   packet 1, sanitizer or composed Stage 2 sign-off, and does not select highlighting
   or persistent-cache package/mechanism choices. Broader fidelity, security,
   indexed-range, scheduling, memory and composed qualification gates remain open as
   listed in the evidence report; queued-job replacement does not stop an in-flight
   parse.
2. **Navigation/read contracts and pure viewport model** — Own contracts in this
   design; backend read ownership in `core/sessions/` and `core/storage/`; the pure
   viewport/anchor model in `source/ui/sessions/`. Define whole-conversation position,
   approximate prompt markers, source anchors, follow intent, reflow correction and
   revision fallback without coupling identity to pixels. Gate: model tests cover
   distant jumps, append/revision changes and height corrections while preserving the
   visible source anchor/following state; reads stay bounded and do not fetch intervening
   bodies or all entry descriptors.
3. **Storage position lookup** — The 100k-per-session flat weighted-position probe
   is recorded in [position evidence](TRANSCRIPT-POSITION-EVIDENCE.md), with raw
   evidence at `%TEMP%/urd-position-probe-5ba1aec72fc9/REPORT.md`. Its indexed
   lookup and marker resolution were correct and fast without body reads or repeated
   whole-history scans; the measured lookup does not justify a hierarchy. The flat
   layout remains a viable baseline, not a production schema selection. Historical
   weight corrections rewrote suffixes at material cost, so maintenance under required
   update patterns remains an unresolved gate. Preserve bounded reads and qualify
   actual write/contention behavior before selecting persistent structure.
4. **Display integration** — Own source/read integration in `core/storage/`, with
   the backend semantic-derivation owner selected after the probes within `core/`;
   own display/viewport integration in `source/ui/app/Transcript.tsx` and
   `source/ui/sessions/`. Gate: Markdown text, code and tables render correctly;
   both entry and intra-message virtualization work, including very large code/table
   content; source-to-rendered anchors and whole-item source reads remain correct;
   byte/piece reads remain bounded. Streaming never flashes raw Markdown. Preserve
   native selection with temporary selected-span retention, keyboard/focus and
   accessibility, and keep input/scrolling/execution responsive.
5. **Bounded shared cache and durable return** — Own browser content/derived-resource
   lifetimes and local navigation state in `source/ui/sessions/` and the existing
   transcript/workspace owners. Gate: share useful derived work across views without
   retaining one full transcript per tab; evicted sessions restore the saved source
   anchor and following intent after tab return and browser reload; selection pins only
   what native selection needs and releases it when cleared. Exercise repeated tab and
   eviction cycles. Set absolute cache/memory budgets only after the probes; do not
   infer a budget from the Stage 1 sample.
6. **Composed qualification** — Own local backend/UI tests and the synthetic browser
   fixture for the complete Stage 2 path. Compose the 100k-entry position case with
   five heavy tabs, one stream, large code/table content, scrolling/reflow, selection,
   reload and eviction. Report backend and browser costs separately and end to end;
   include failures and contention. Gate: check the provisional input, visibility,
   long-task and warm/derived-cold targets below, plus bounded reads, accessibility,
   source-anchor/follow preservation, no raw-Markdown flash and memory release. A failed
   target remains open evidence for tuning/review, not an automatic relaxation.

Every packet includes meaningful local tests, failure-path checks and independent
review where practical. Composed Stage 2 qualification does not postpone memory/latency
checks in earlier packets. Later cross-stage qualification is not permission to defer
Stage 2 acceptance. A completed intermediate source slice is not automatically an
approved replacement for the running user application. No production activation,
user-data migration or live-model test follows from this plan.

Still unqualified before claiming the overall design works: scrollbar geometry and
anchor stability under reflow; very large individual inline/table-cell structures;
selection during streaming/eviction; parser scheduling under execution contention;
image/GPU resource release; and chosen provider draft/reasoning/image observation
capabilities. Resolve these at their owning slice, not by promising a universal
memory ceiling or creating speculative frameworks upfront.

Requirements check additions (2026-11-09), owned by the named stage:
- **Provisional performance targets (decision 18).** Local machine, measured from
  stage 1 onward and revised from evidence, not silently relaxed:
  - Input: keystroke painted within one frame (~16 ms), including while streaming.
  - Streaming: committed model text visible ≤100 ms after arrival at the backend;
    may require a shorter output flush interval than today's 100 ms.
  - Smoothness: no main-thread task >50 ms during streaming or scrolling; huge code
    blocks/tables are formatted and mounted incrementally.
  - Tab switch: ≤33 ms when content is warm; ≤100 ms cold when derived structure
    exists. First-time formatting of a huge message is the stated exception.
  - Idle: no periodic work beyond a ~30 s connection heartbeat.
  - Backend: transcript formatting runs as one low-priority shared job and never
    delays model or tool execution; large messages wait instead.
  - Memory: transcript memory independent of history length and returning to
    baseline after detail close/tab cycles. Stage 1 now has a bounded baseline, but
    it does not establish recovery or an absolute budget; defer numeric cache/memory
    budgets until Stage 2 probes and composed evidence.
- **Animation constraints for the future UI design (stages 2–3):** performance
  rules, not visual choices. Animate transform/opacity only; never animate height
  inside the virtualized list without anchoring the reader; state changes are not
  delayed by animation; reduced motion honoured. Measure dropped frames while
  streaming, expanding rows and promoting pending input.
- **Small-message latency (Stage 2):** most replies are small. Measure time from
  committed text to formatted display through the backend-structure path; the
  large-message design must not make the common case sluggish.
- **Syntax highlighting ownership (Stage 2):** unassigned. Compare backend tokens
  with browser highlighting, including large-code-block cost and lazy offscreen ranges,
  in the probe matrix before choosing a library.
- **Tool payload size (stage 3):** stored tool arguments/results are currently capped
  at 16 KiB each, below real model-facing results (decision 26). Inspection needs
  bounded reads once that execution-layer limit is raised; do not design for small
  payloads only.

### Current Stage 1 baseline and open limits

The Stage 2 position probe used 100,000 entries in each of five interleaved sessions;
see [position evidence](TRANSCRIPT-POSITION-EVIDENCE.md) for lookup, storage and
historical-correction costs. It supports no hierarchy for lookup, but does not resolve
historical-maintenance requirements. The current-tree 25× Stage 1 run is a baseline,
not sign-off. The five heavy sessions used 100,000 entries each. The implementation report records first-mount switch
median **112.4 ms**, return/remount median **130.3 ms**, commit-to-DOM-plus-next-rAF
bracket **39.4–44.8 ms**, **22.74 active GET/s**, and at 60 seconds **+17.24 MiB
browser / +4 MiB backend** over baseline. The commit bracket is not pixel latency;
the remounts are not Stage 2 warm-cache measurements. An intermittent tab-return
anchor assertion remains unroot-caused (0/25 focused reproductions); it is not signed
off. These observations establish neither memory recovery nor overall performance.
See the [current Stage 1 implementation report](transcript-history-implementation.md)
for methods, ranges and caveats.

## Verification approach — broader work proposed; isolated parser probe completed

- Measure disk queries, decoding/projection, transport, parsing, layout/render,
  retained browser memory and backend cost separately and end to end.
- Distinguish cold startup/restoration from warm tab switching. Measure time to
  usable actual content, not just a loading indicator.
- Use long mixed history, huge individual replies/code/tool results, concurrent
  streaming, hundreds of calls, user screenshots, slow loads and failures.
- Repeat tab changes, scroll away/back, inspection open/close and image viewing;
  memory should settle rather than grow each cycle. Include native decoded-image
  resources, not just JS heap or bundle size. No arbitrary budgets chosen yet.
- Check reading anchors while prepending history, correcting measurements, loading
  images, streaming Markdown, expanding detail and changing viewport/zoom.
- Use isolated local fixtures and synthetic data; no live provider/model requests,
  real credentials, user data mutation or activation of a build without separate scope.
- Verification stays local; do not add CI tooling (current AGENTS.md).

[Browser design](BROWSER-DESIGN.md) records a bounded synthetic plaintext experiment,
not qualification of rich rendering, long individual messages or real SQLite under
agent load. Its heap sample is not full browser memory or a soak result.
[Durable boundaries](DURABLE-BOUNDARIES-EVIDENCE.md) records managed-file publication,
shared-reference and cleanup failure cases; those are not implemented guarantees.

## Discussion record and rejected shortcuts

- Owner corrected an unnecessary reopening of managed attachments: existing
  architecture already establishes that direction. Keep it, discuss mechanics.
- Owner accepted external linked pictures remaining external, without automatic
  retained copies; no mandatory save-picture action initially.
- Grilling decision 1: owner chose whole-conversation scrollbar navigation and
  called out estimated heights plus future user-prompt markers along the scrollbar.
  Markers should appear at approximately correct vertical positions. This is not
  approval to render/load all history, nor a promise of exact unmeasured geometry.
- Grilling decision 2: owner approved eviction rather than a warm window for every
  open tab. Suggested warming on hover for tabs and similar on-demand interactions;
  exact triggers/work budgets remain engineering proposals, not blanket background
  fetching permission.
- Grilling decision 3: owner approved restoring reading position after browser
  reload/restart using small local navigation state; full content can still be evicted.
- Grilling clarification 4: “raw tool input/output” was imprecise. Owner wants
  inspection generally to reflect what the model produces/sees, after any pruning
  such as whitespace/format reduction and omission of huge output to protect context.
  The earlier original-tool-result default proposal is superseded. This clarification
  does not yet settle pre-pruning retention/recall or approve a reduction algorithm.
- Grilling boundary 5: owner assigns large-output omission, omission notices and
  retrieval instructions to the pruner, not the UI/transcript. Do not build a pruning
  system in this work or ask the owner to settle its raw retention policy here.
  Transcript loading/eviction must not masquerade as model-context pruning.
- Grilling ordering direction 6: queued/pending user input does not reach the model
  immediately and should be treated separately. Only place it as a user message
  when it “hits” the model. The previous assistant proposal assumed immediate
  admission-ordered user display; that assumption is superseded. Promotion timing
  needs a precise, observable definition and the public history projection needs
  change; this does not authorize changing input admission/delivery guarantees.
- Grilling decision 7: after a plain-language failure example, owner confirmed that
  once Urd starts sending a pending message, a connection/request failure leaves it
  in the conversation with the error rather than returning it to the queue. This
  resolves the user-visible promotion/failure behavior, not the exact provider-hook
  or storage transaction implementing it. Do not imply proven remote consumption.
- Grilling decision 8: owner deferred search; a first-party search feature can later
  replace browser Find. No full-history search/index work is required now, and
  virtualization must not be defeated merely to make browser Find exhaustive.
- Grilling challenge 9: owner expects investigation of ordinary long-distance
  selection, suggesting temporary extra retention, rather than prematurely limiting
  selection to a local window. Read-only public documentation research identified
  render-range retention and model-owned selection patterns. No package, hard cap,
  custom selection system or prototype implementation has been selected.
- Grilling direction 10: owner said to adopt whatever is simplest. Proceed with
  native selection and temporary selected-span retention, not a speculative custom
  selection engine or separate prototype phase. Verify during implementation;
  revise only for demonstrated problems, not hypothetical scale alone.
- Grilling decision 11: owner approved automatic loading of external Markdown images
  near the viewport, rather than requiring clicks. External retention remains unchanged.
- Grilling decision 12: owner approved compatibility now, features later for maths
  and diagrams. Choose extension-friendly parsing/rendering boundaries without
  implementing optional renderers or a general plugin system in the initial scope.
- Grilling decision 13: owner approved pursuing backend-derived formatting structure
  with browser-owned rendering/layout. Source stays authoritative and derivations
  disposable. Approval is for responsibility boundaries, not a package/schema or
  proof of bounded parsing cost; validate library capabilities next.
- Grilling decision 14: owner approved formatted catch-up updates when a growing
  reply outpaces formatting, rather than briefly showing raw Markdown. Scheduling
  must not block input, scrolling or execution; no fixed latency target is selected.
- Owner explicitly chose continuous formatting rather than a separate paged
  plaintext fallback for very large replies. Earlier suggested 128 KiB/message and
  2 MiB cache numbers were speculative and are **not selected requirements**.
- Owner requested engineering discussion, not repeated abstract appearance choices.
  Tool rows/inspection should use Pie as a reference; investigate rather than asking
  the owner to redesign the familiar interaction from scratch.
- A limited rendered Pie inspection confirmed compact icon/argument rows, status
  placement and muted inline reasoning. It did not qualify live draft streaming,
  expansion behavior or underlying architecture/performance. No Pie source/styles
  were inspected/copied. Temporary browser/computer sessions were closed.
- No Stage 2 source implementation, dependency, cache budget, schema, worker design
  or runtime performance guarantee has been approved here. Push change notices are
  agreed in principle (decision 16); their implementation is not yet approved.
- Grilling decision 15 (requirements check, 2026-11-09): owner reconfirmed tool
  details show **model-facing** input/results only, not process I/O. “Raw tool IO”
  in conversation means this; do not reintroduce a raw execution-log feature.
- Grilling decision 16: owner approved replacing 350 ms snapshot polling with
  lightweight server-pushed change notices in stage 1. Notices say “changed”;
  content is still read on demand. Polling lag and repeated preview rereads
  contradicted snappy streaming and low overhead.
- Grilling decision 17: managed attachment images and viewers stay in Stage 4;
  external linked Markdown images are a separate Stage 2 fast-follow after core text,
  code and table qualification. Urd is not yet used for real work; the goal is a
  strong foundation rather than early daily-use features.
- Grilling decision 18: owner adopted the tightened provisional performance targets
  listed under "Requirements check additions"; absolute memory budgets follow Stage 2
  probes, not assumptions from the Stage 1 baseline. Owner accepted that tighter
  streaming latency may cost more writes.
- Requirements check found stage 1 indexed only text messages, so tool calls (the
  dominant row type) had no anchors/order. Stage 1 now indexes kind-tagged entries.
  Dense message ranks and a persisted change version were dropped from stage 1:
  rank is not scroll geometry, and per-chunk summary writes cost the hot path.
  Stage 2 first benchmarks weighted-position lookup at 100,000 entries before adding
  any hierarchy or other persistent derived structure.
- Grilling decision 19 (second requirements pass, 2026-11-09): a model reply's
  text runs, tool calls and (later) reasoning are separate entries **in the order
  the provider produced them**, e.g. text → call → text → call. One reply is not
  stored/displayed as all text followed by all calls. Stage 1's entry index must
  allocate entries in observed order, so a text run closes when a call begins.
- Grilling decision 20: reasoning summaries are saved for display and remain
  visible after reload/eviction, as their own entries under the same retention and
  bounded-read rules as other text. Opaque continuation data stays private.
- Grilling decision 21: subagents follow Pie's interaction. The parent shows a
  subagent **card** entry with a preview and streamed output; expanding it shows the
  full subagent transcript nested in the card, in a bounded-height panel with its
  own scroll and its own on-demand loading (same transcript component, child session
  as source). The parent's scroll geometry is not inflated by child entries.
- Grilling decision 22: compaction leaves a compact **marker entry** at the point
  it took effect; the summary the model received is inspectable on demand. Original
  entries above it stay readable.
- Grilling decision 23: editing an earlier prompt **permanently discards** every
  entry after it. No Undo and no viewable previous versions. History stays linear;
  a revision change invalidates later anchors, and a reader whose anchor was
  discarded falls back to the edited prompt.
- Grilling decision 24: user messages render as Markdown, like assistant text.
- Grilling decision 25: images a tool returns to the model are **on-demand only**
  (details, expansion or rich tooltip), never full-size in a collapsed tool row.
  Exact placement waits for the tool-row design in stage 3; no image is decoded for
  a row until its detail is requested.
- Grilling decision 26 — reference workload. Read-only stats over 1,033 Pie
  sessions (8 Sep–9 Nov 2026; subagents separate; Codex/older sessions excluded;
  message content only, not file size): per session p99 1,441 entries, 924 tool
  calls, 2.6 MB tool results, 8 images; largest seen 2,421 calls, ~4,000 messages,
  ~6 MB content, single tool result 196 KB, single reply 40 KB. Owner believes real
  peaks were larger, so this is a lower bound. Adopted **25× headroom** fixture:
  ~100,000 entries (~60,000 tool calls), ~250 MB content, single 5 MB reply and
  5 MB tool result, ~1,000 images, five such tabs plus one streaming. Nothing
  proportional to session length may be held in browser memory casually.
  Observation: Urd's 16 KiB stored tool-result cap is below the median session's
  largest model-facing result (33 KB); details must not assume small results.
  Same data: the largest tool **arguments** exceed 16 KiB in about 10% of sessions
  (p90 16 KB, max 64 KB, e.g. whole-file writes), which Urd's current 16 KiB argument
  cap would reject. These are execution-layer limits outside transcript scope, but
  the transcript must not bake them in.
- Grilling decision 31: lift the 16 KiB caps on tool arguments, tool results and
  user submission text in stage 1's migration, with no replacement ceiling ("we can
  add it back if we need to"). Bounded reads, not write caps, protect display paths.
- Grilling decision 27: entries record when they happened; tool calls record start
  and end; each model reply records token usage and estimated cost. Captured now so
  later hover/detail views have data. Observed: `LlmResponseAccumulator` currently
  discards `UsageContent`, so usage capture is new backend work.
- Decision 27 follow-up (2026-11-09): usage capture is **harness-owned**
  (execution/storage, on the request row), also feeding the agreed post-closure
  usage summaries; it is not a transcript entry fact. Stage 1 adds no usage read
  contract — later detail views read on demand by request ID. Only provider-reported
  usage is captured; estimated cost waits on a pricing source.
- Grilling decision 28: a running tool may expose live progress output. It is
  transient (not saved), bounded to the latest output, delivered only while its
  session is observed, and replaced by the model-facing result on completion. Only
  tools that report progress provide it; model-facing inspection is unchanged.
- **Owner steer (2026-11-09): UI decisions stay open.** This phase defines the
  architecture and backend support the future UI needs (data, identities, reads,
  live updates, lifetimes), not layout, controls or interaction details. E.g. a
  message copy action is undecided; the backend must simply be able to serve an
  entry's full original source in bounded pages. Ask the owner only questions that
  change architecture/backend contracts.
- Engineering consequences of decisions 19–28 (proposed, not owner decisions):
  - **One push stream per browser page, multiplexed by session.** Browsers allow
    about six HTTP/1.1 connections per host; a parent plus streaming subagent cards
    would exhaust them with per-session streams and stall ordinary reads. The page
    tells the server which sessions it is watching; notices name the session.
  - **Observed order needs provider support.** Calls are currently collected at
    completion and limited to 16 per reply; adapters must surface output-item order
    where the provider exposes it. Where it does not, fall back to text-then-calls
    rather than fabricating order.
  - **Subagent cards** need a cheap child-session preview/tail read and child
    notices on the same stream, without loading the child's history.
  - **Fan-out load.** Architecture's scale direction (~50 live agents direct, ~210
    deep) applies: the reference workload adds ~50 concurrently streaming sessions
    of which one is visible with a few expanded subagent cards. Unobserved sessions
    must cost no transcript parsing, notices or browser work.
- Grilling decision 29: a tool entry stores **one** result: what the model was given
  when the result entered its context (after any reduction applied before delivery).
  Later context reductions are not tracked or shown on the entry; no second copy and
  no reduction history. Compaction markers (decision 22) remain the context signal.
- Grilling decision 30: for pasted/uploaded images Urd stores **only the version the
  model receives**. Urd performs any downscaling itself so the stored file is exactly
  what was sent; the full-resolution original is not retained. Proposed engineering:
  also store one small preview per image at save time so compact views never decode
  the model image.
- Proposed engineering: the multiplexed page stream also carries cheap status
  changes (running/idle/failed/needs attention) for every open session, so tabs and
  cards stay truthful without content reads; content notices only for watched ones.

## Continuation and safety

Current design state: Stage 2 scope and review packets are recorded. Bounded parser
and weighted-position probes now support process-local reuse/context-aware highlighting
and a flat lookup baseline without a hierarchy; neither packet is signed off, and
historical position-maintenance requirements remain unresolved. See [parser evidence](TRANSCRIPT-PARSER-EVIDENCE.md)
and [position evidence](TRANSCRIPT-POSITION-EVIDENCE.md), including links to raw
reports. All remaining qualification targets remain open. Navigation covers the whole conversation; future prompt markers
share its estimated position model. Inactive-content eviction is approved; hover warming remains a hint,
not a requirement to mount or retain content. Small local reading anchors/following
intent survive browser reload/restart. Tool inspection defaults to model-facing
content including reductions, not raw execution I/O; the pruner owns omission/recall.
Pending user input promotes when sending a containing request starts; failure does
not automatically requeue it. Full-history search remains deferred. Preserve native
selection with temporary selected-span retention; do not add a custom selection engine
without demonstrated need. External linked Markdown images load automatically near
the viewport, but only as a fast-follow after Stage 2 core qualification; managed
attachments/viewers remain Stage 4. Existing unfinished tool-draft crash behavior,
formatted catch-up (never flash raw Markdown), and optional math/diagram compatibility
remain unchanged. Stage 2's approved direction is backend semantic structure with
browser rendering/layout; the parser probe is preliminary evidence, not package or
persistent-projection selection. Its bounded packets, 100k-entry SQLite position
lookup baseline, gates and current Stage 1 measurements are recorded above. Stage 1's
current-tree 25× run qualifies only that bounded workload; its tab-return anchor
assertion remains unroot-caused and its memory sample is not recovery sign-off. Stage 2
integration, browser behavior, scheduling and cache budgets remain unqualified.
Do not invent a custom parser or treat sampled sizes as hard limits. Do not keep
asking minor product questions that can be resolved through existing requirements.
Ask only unresolved questions,
not questions already settled by position-preservation requirements. Continue down
the dependency tree and keep recommendations distinct from decisions.

Do not access/mutate `C:/dev/data/urd` or exercise live account/model requests.
The existing user application was on port 55174/PID 2548 during the initial
inventory; identifiers can change and are not permission to operate it. Do not
inspect/copy Pie UI source/styles. Visible control requires appropriate user scope.

Other work: frontend audit cleanup has landed through 5c9392f, including Workspace
ownership boundaries, stylesheet separation and offline rendered regressions. Its
reported anchor failure was resolved as test setup/timing, without Transcript behavior
changes. This session remains planning-only in the transcript design/evidence/plan
documents. Earlier parent work covered palette/tabs and provider/settings integration.
Recheck ownership before any source edits.
Future transcript logic can stay feature-local; coordinate shared CSS, Workspace,
package/lockfile and docs changes. Preserve unrelated working-tree edits.
