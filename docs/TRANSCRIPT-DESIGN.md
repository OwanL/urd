# Transcript engineering — working design

**Status: requirements agreed in discussion; architecture under examination.**
**Updated: 2026-11-08.** No renderer, virtualizer, transport, cache budgets,
new schema or implementation milestones have been selected. This document is the
working record for continued design discussion, including after context compaction.

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

- Rich Markdown/code, embedded images and image viewing; extensible to further
  structured content without treating everything as specially formatted text.
- Long-running sessions mixing hundreds of tool calls, substantial reasoning,
  normal assistant output, large code blocks and large tool results. These are
  representative workloads, not hard caps.
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
- Long replies remain in the **continuous formatted conversation**. Diverting
  oversized replies to a separate paged plain-text reader is not acceptable.
- Reading position and following intent should survive browser reload/restart.
  Retain small local navigation state, not full transcript content; no cross-client
  navigation synchronization is required by this decision.
- Inactive tabs may evict transcript content and briefly reload their reading
  window when revisited. Preserve the reading anchor and following intent; keeping
  every open tab warm is not required. Owner suggested predictive warming on hover
  as a general interaction pattern, not something restricted to tabs.
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
  save-picture feature is not required initially. Automatic external loading versus
  explicit activation remains unresolved; do not silently introduce remote fetching.
- Provider-supplied formatted reasoning summaries appear inline, visually muted
  but readable. Unsummarized reasoning exposed by a provider is collapsed and
  inspectable. Opaque/encrypted continuation state is not public transcript content.
- Tool input must stream into the transcript as the model constructs the call,
  before execution. Providers that do not expose incremental input must not get
  fabricated streaming. Drafting a call is not authorization to execute it.
- Tools use compact rows, generally one per call, with on-demand deeper inspection.
  Tool icons also expose rich hover inspection of raw input/output. Interactive
  details remain reachable for selection/copy, keyboard and click, not fragile
  hover-only text. Their data/rendering lifetime belongs in this design.
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
  immutable `execution_output.text_utf8` segments. Submission text is limited to
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
including parallel tool calls and visible admission order versus model-delivery
order. Derive display blocks without making generated HTML the source of truth.
Define revision invalidation for edits and how a removed anchor falls back.

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

Keep partial tool input distinct from validated saved calls. Correlate draft and
final identity; preserve positive provider-terminal and cleanup requirements for
execution. Incomplete argument text need not be valid JSON. Decide what draft
state survives disconnect/restart, and how a new observer obtains current state
without replaying an unlimited event log. No transport choice follows yet.

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
or perform command side effects. External-image loading policy remains separate.

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

### Markdown and large payloads

Transport pages are not independent Markdown documents. Fences, lists, tables and
reference definitions can cross boundaries; later content may change earlier
interpretation. Investigate parsing correctness, work scheduling and retained
structures independently of DOM virtualization. One huge code block/table also
needs attention; splitting only at top-level Markdown blocks is insufficient.

Use proven parser/rendering components rather than a homemade Markdown parser.
No package is selected. Earlier discussion suggested a unified/mdast/hast pipeline,
but this was not adopted or qualified; compare alternatives after defining needs.
Do not assume workers are automatically better: transferring/copying content and
maintaining extra trees can increase memory. Benchmark before adding them.

### On-demand details and images

A compact tool descriptor must not carry full input/output in every history update.
Opening inspection requests relevant content; huge payloads and live tails need
bounded reads/rendering. Closing ends unnecessary subscription/rendering work;
cache retention, if useful, must be explicit. Preserve original captured content;
a formatted JSON display is not a substitute for the raw evidence that exists.

Open capture boundary: a tool's returned result, Urd's later model-facing reduction,
and all output emitted by an underlying process/service are different things.
[Context lifetime evidence](CONTEXT-LIFETIME-EVIDENCE.md) distinguishes raw execution
results from reduced model-facing results and does not establish universal upstream
capture. [Storage research](STORAGE-RESEARCH.md) likewise leaves capture fidelity open.
Do not accidentally promise complete subprocess logs merely by labeling an inspector
“raw output”; do not present a reduced model-facing summary as the captured original.

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
3. **Content ownership/identity/order — current branch:** first clarify what raw tool
   output denotes: captured tool result before Urd reduction versus all underlying
   process/service output. Existing docs deliberately do not promise universal raw
   capture. Recommend preserving captured results and explicit artifact references,
   without silently adding an unlimited process-log archive. Then resolve message/
   content-part/tool relationships, interleaving, draft-to-final identity and revisions.
4. **Streaming recovery:** what the reader sees after interruption during tool-input
   generation; provisional versus retained evidence, cancellation and terminal facts.
5. **Large-item correctness:** Markdown semantics, code/table virtualization,
   streaming parse strategy and memory ownership. Prove feasibility before choosing.
6. **Interaction through eviction:** selection/copy, keyboard navigation, browser or
   application find, expanded details and resize. Do not silently break existing UX
   or assume full-history search is already approved.
7. **On-demand payloads/media:** inspector lifecycle, large raw results, clipboard
   uploads, attachment publication/cleanup, image preview/viewer release, external
   URL loading policy. Display rendering does not imply provider image delivery.
8. **Budgets and qualification:** representative workload sizes, numeric latency
   and resource targets, cache/overscan policies, contention and failure cases.
9. **Implementation sequencing:** bounded milestones and ownership only after
   the above contracts and high-risk feasibility checks are coherent.

## Verification approach — proposed, not executed

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
- No source implementation, dependencies, cache budgets, push/poll transport,
  schema, worker design or runtime performance guarantee has been approved here.

## Continuation and safety

Current task: engineering grilling and durable planning, not implementation.
Navigation semantics (#1 above) is resolved: whole conversation, with future
prompt markers sharing the position model. Inactive-content eviction is approved;
hover warming is desired direction. Local reading anchors/following intent must
survive browser reload/restart. Current branch: raw tool-result capture boundary,
then content identity/order. Ask only unresolved questions,
not questions already settled by position-preservation requirements. Continue down
the dependency tree and keep recommendations distinct from decisions.

Do not access/mutate `C:/dev/data/urd` or exercise live account/model requests.
The existing user application was on port 55174/PID 2548 during the initial
inventory; identifiers can change and are not permission to operate it. Do not
inspect/copy Pie UI source/styles. Visible control requires appropriate user scope.

Other work: parent handled palette/tabs and `workspace.css`; provider/settings peer
handled Settings/Workspace integration. Recheck current ownership before changes.
Future transcript logic can stay feature-local; coordinate shared CSS, Workspace,
package/lockfile and docs changes. Preserve unrelated working-tree edits.
