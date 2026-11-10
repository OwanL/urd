# Dynamic tool discovery: direction and qualification

**Initial comparison: 2026-11-03; owner alignment integrated 2026-11-04.**
[ARCHITECTURE.md](ARCHITECTURE.md) owns decisions. `IChatClient` and shared SQLite
are selected; a narrow Urd-owned model/tool cycle is provisional. The owner now
prefers **qualified native appended definitions, otherwise a stable invocation
wrapper present from the initial catalog**. This supersedes the earlier proposal
to use ordinary tools-list updates as the portable fallback. It does not select
concrete provider adapters, discovery triggers, class APIs or a production schema.

**Initial implementation scope (owner clarification 2026-11-10):** Dynamic tool discovery
and tool/skill pruning come later. For now, all configured tool capabilities are available
up front through their selected surfaces, not every variant. Future model-family aliases
replace their generic surfaces; see [agent tools](agent-tools-design.md). The direction below
is retained for that later discovery/pruning work, not a prerequisite for the initial tool
implementation.

## One execution path, two exposure routes

1. **Select from the live catalog.** Retain AI-based tool/skill pruning. Exclude
   protected core/pinned entries before presenting candidates to the pruner;
   discovery/recovery complements it. Keep stable tool/source identity and the
   complete selected definition.
2. **Expose without rewriting earlier definitions.** Where provider **and adapter**
   support qualified native appended definitions, use them and omit the invocation
   wrapper from the initial catalog. Otherwise include the wrapper from the start
   and append recovered-tool descriptions to context. Initially selected native
   tools/core capabilities need not all become wrapper calls. Pin definition/order
   and model-context position; historical exposure is not current permission.
3. **Dispatch through current authority.** After positive provider-terminal
   evidence, resolve the native call—or wrapper's resolved target—against the same
   live executable/permission path. Preserve the provider's outer call ID/name and
   resolved tool identity distinctly. Record the actual result under the matching
   call ID before continuing. Complete-looking arguments, HTTP success or stream
   EOF alone cannot authorize effects.
4. **Restore context, not effects.** Reconstruct exposure and call/result history,
   including required provider-native items; rebuild live implementations and
   permissions separately. Never repeat a completed or uncertain side effect just
   to repair the transcript. Remembered work is not replay permission.

This **tool-invocation wrapper** is not a mandatory model-client facade.
[Session design](session-design.md) composes request-only `IChatClient` directly
for execution and auxiliary operations. Both exposure routes share execution;
no per-provider session engine or general discovery framework is selected.

## Route evidence and gaps

Pins and detailed source findings remain in
[model-interface research](model-interface-research.md). These findings qualify
specific mechanics, not live-provider acceptance or cache savings.

| Route | Native path and established evidence | Fallback / remaining qualification |
| --- | --- | --- |
| **OpenAI Responses** | `additional_tools` can represent appended client-executable definitions. OpenAI .NET 2.14.0 lacks public typed support; MEAI 10.11.0 uses raw unknown items. The [composed execution fixture](execution-design.md#composed-follow-up-adapter--guarded-urd-loop--sqlite--continuation) exercises the real adapter/terminal guard/loop/tool with ordered native reconstruction through SQLite test-envelope reload. | Experimental adapter; native IDs, terminal status and raw persistence need explicit handling. Actual session-store integration, process-kill recovery, production streaming and native provider acceptance remain unqualified. Use the initial wrapper on routes without qualified native support. |
| **Anthropic / Claude** | Provider documents inline `tool_addition`/`tool_definition` beta blocks, subject to model/placement restrictions. SDK 13.4.0 has typed beta models, but its ordinary `AsIChatClient` adapter drops those blocks despite preserving mid-conversation system text. | Native append needs separate adapter qualification; no Claude beta adapter is approved here. Stable wrapper is the selected fallback, not ordinary-list updates. |
| **OllamaSharp / local models** | The inspected 5.5.0 adapter maps ordinary function declarations; no native append equivalent was established for that route. Actual tool support, thinking replay and call identity require server/model/adapter testing. | Include the stable wrapper initially for recovered tools where function calling works. Wrapper reliability is untested; do not generalize this adapter finding to every local/OpenAI-compatible server. |
| **Other routes** | Native appends require exact provider+adapter evidence; none is implied by implementing `IChatClient`. | Initial-wrapper fallback also needs tool/model qualification; unsupported tool calling is not repaired by a wrapper. |

**Route distinction:** OpenAI `tool_search` is a separate hosted/client-executed
facility on eligible routes, not equivalent to Urd's client-controlled appended
exposure. The documented **ChatGPT-plan route forbids `tool_search`**, while
allowing `additional_tools`; see [provider qualification](provider-qualification.md).
Execution's synthetic `tool_search_output` item tests an unknown-item codec only,
not a valid plan request. The original SDK JSON-only fixture was weaker than the
later adapter fixture; neither is a real-provider round trip.

Ordinary tools-list updates remain a consciously available alternative, **not the
selected fallback**. The initial-wrapper rule favours prefix stability, not measured
savings. Do not insert a wrapper late, silently switch exposure after a rejection,
or replay tools as a fallback. The [resumed owner review](#resumed-owner-review--2026-11-05)
below narrows provider-switch behavior without selecting a reset implementation.

## Resumed owner review — 2026-11-05

### Owner answer: switching away from provider-only reasoning

Concrete scenario: an idle chat switches from OpenAI to Claude, and the old
provider's saved private reasoning cannot be passed to the new model. Offered
**warn and continue** versus asking for confirmation; recommended continuing.
The owner answered:

> This is fine, power users will understand this anyhway, and the full reasoning
> traces are not usually needed for session continuation anyway, lets not get
> hung up on this

**Outcome:** do not block an explicitly chosen model/provider switch merely
because private reasoning cannot transfer. The warn-and-continue direction was
accepted; no particular notification UI was selected. This is not permission to
silently lose visible messages, tool results or attachments, nor to rerun tools.
Readable originals remain available while the chat is open. Running-turn change
admission belongs to Session Design; own-chat automatic provider fallback and a
wider provider/billing pool are not approved by this answer.

### Owner steering: simplest loading behavior; details wait for implementation

For instruction-file edits, the owner first favored explicit reload over automatic
next-message updates: “Reload is probably simplest”, citing cache preservation.
They then reiterated “just ignore changes until reload, saving the runtime loading
complexity”. Asked to distinguish that from post-compaction selection, they said:

> skills are different, in that changing skills does not break cache. I suppose
> changing instruction files depending on implementation would also get this
> behaviour if transcripts stamp an immutable copy of instructions, and things
> are never changed from there, regardless, i think it is too early to tell on
> this wihtout implemnetation. The decision pretty much lies with whatever is
> simplest

**Latest outcome:** prefer simplicity; defer exact loading/reload mechanics until
implementation makes the trade-off concrete. The earlier reload preference is
not approval for a new per-chat reload control, file watcher or live-loading
subsystem, nor a requirement to freeze every session forever. Existing ordinary-
continuation prefix stability, deliberate changes and readable originals stand.
Do not turn every file edit/runtime event into a mandatory reset confirmation.

**Engineering clarification, not another owner answer:** skill metadata already
included in instructions is part of the earlier prefix; rewriting it can break
cache reuse. Loading a skill body as a new tool result appends content instead.
Editing its source file must not retroactively replace a body already delivered.
Thus “skills do not break cache” is true of append-only loading, not all skill
changes. File loading, capability selection and model-context rebuilding are
separate concerns; none grants execution permission.

### Concrete next-request behavior — reference-based proposal

These recommendations preserve inspected Pie behavior where applicable. They are
**not additional owner decisions**, approved APIs or prerequisites for the unrelated
first fake-model slice. No new context engine, pruner or serializer is authorized.

| Scenario | Proposed behavior and limit |
| --- | --- |
| Ordinary next reply or reopening the same chat | Reuse already-sent guidance, ordered definitions and result representations; append new content. Reopening alone does not reread files into old content or rerun selection. Current permissions still apply. |
| User explicitly changes an idle chat's model/provider | Keep its selected capabilities where still eligible; adapt the working context to the chosen route without rescoring merely because the model changed. Apply the private-reasoning owner answer above. A changed route can start a new initial catalog with the required wrapper; this is an explicit boundary, not late insertion into the old prefix. Visible-content incompatibility must not be silently treated as successful transfer. |
| Instructions/resources are deliberately reloaded | Use the changed guidance at that explicit boundary without treating reload as a request to rerun AI selection. Ordinary history stays readable. Initial load plus ordinary explicit reload is the simple candidate; exact reload scope/timing remains deferred by the owner. |
| Successful manual or automatic compaction | Use the summary and retained recent messages. With pruning and post-compaction refresh enabled, reselect before the next model request, not merely because the chat is idle. With refresh off, keep the previous selection, not pre-compaction history. Unchanged earlier instruction/tool content need not be rewritten just because history was compacted. |
| Failed/cancelled compaction | Keep usable existing context; do not establish a successful-compaction boundary or claim a refreshed selection. |
| Explicit selection refresh or capability recovery | Refresh deliberately reconsiders the catalog; recovery appends an eligible tool disclosure or loaded skill body without rescoring the catalog. Protected entries remain excluded before pruning and protected when applying results; necessary recovered definitions cannot depend on summary prose remembering them. |
| A tool is disabled, removed or incompatibly changed | Current authority wins even when an old definition is still visible. Deny stale/unauthorized execution; do not rewrite history or silently rebind the remembered definition. A context boundary cannot repair or replay an uncertain effect. |

Pie's settings default post-compaction refresh on, but pruning itself off; these
checked-in defaults are not usage evidence or a new Urd configuration decision.
Its expected selection-failure fallback exposes all currently available candidates,
with a diagnostic, rather than retaining the prior pruned set. That fallback is
source evidence, **not approved here as Urd's failure policy**; it can change model
exposure without changing execution permission. Failure behavior remains explicit
future qualification, not a reason to add another owner questionnaire now.

### Responsibilities, evidence limits and smallest next step

- **Context owner:** retain the already-sent working view and readable originals
  distinctly; apply deliberate changes without confusing selection with history.
  No concrete class or storage shape is selected.
- **Selector/recovery:** protected-candidate handling, explicit selection triggers
  and necessary disclosure carry-forward; no new billing/provider eligibility.
- **Request adapter:** reconstruct supported native items/results and choose the
  qualified native route or initial-wrapper route. Do not claim foreign private
  reasoning is portable or use a wrapper to repair unsupported tool calling.
- **Live execution/session owner:** check current authority; own running-change
  admission, cancellation and uncertain-effect continuation. This discussion
  neither changes Lifecycle's remit nor grants automatic own-chat fallback.

Read-only reference inspection at Pie `cc82abb0f317f31341d3a6f12aa1545f6be1fbea`:
`docs/plans/IMMUTABLE-MODEL-CONTEXT.md:23–53,161–168`;
`harness/tool-and-skill-selection/state/context-period-state.ts:173–206`;
`lifecycle/register.ts:231–324,424–470` and `settings/config.ts:20–28` under that
same selection directory; `harness/pi/packages/coding-agent/src/core/agent-session.ts:2402–2422,2765–2788`.
Selection failure: `prepass/prepass.ts:694–730,794–813,1005–1045` under the selection
directory. These are source/contract observations, not installed-host tests.

**Smallest next qualification, when an actual context owner exists:** take one
explicit model-change or reload boundary through that owner, then an ordinary
next request; verify the permitted change does not also rescore, lose readable
originals, broaden authority or replay tools. Include the already-sent-result
invariant below through its real durable representation. Do not repeat the
standalone fixture or add a live-model/cache programme to this review.
Visible-content incompatibility handling, selection-failure policy and precise
reload/admission semantics remain open; no further consequential owner choice
was forced in this round.

The already-sent-result drift in [Context Lifetime Evidence](CONTEXT-LIFETIME-EVIDENCE.md)
is an engineering acceptance gap, not an owner vote on serialization fields.
Its replay control is not a selected production recorder or storage design.

## Minimum facts to restore

These are logical facts, not a schema or an event-sourcing requirement:

- **Exposure:** stable tool/source identity, definition version and exact exposed
  schema/order/context position; native items or wrapper disclosure needed to
  reconstruct what the model saw. Preserve necessary native material in Urd-owned
  records: `IChatClient` is not a durable serialization format.
- **Invocation/result:** original provider call ID, outer name/arguments, resolved
  target and definition identity, sequence and exact model-visible result/error.
  Do not synthesize an identity that breaks call/result pairing.
- **Execution evidence:** distinguish no recorded dispatch, completed execution
  with a saved result, and an uncertain effect. A missing result does not prove
  an action never happened; a complete call draft does not prove response success.
- **Current authority:** reconstruct live bindings and permissions independently.
  Preserve old definitions when a schema changes or permission is revoked, but
  block incompatible/unauthorized execution rather than silently rebinding it.

Restore recorded results without rerunning tools. Orphaned calls that may have
reached an external tool need deliberate resolution. The recovery bar remains
coherent continuation, not exact token retention or a workflow-replay engine.

## Remaining gates and focused acceptance

- **Prune/expose/dispatch:** protect core entries, recover a tool on each exposure
  route, and use one permission-checked execution path. For wrapper calls preserve
  both outer call and resolved target; qualify actual model reliability.
- **Adapter fidelity:** verify ordered native definitions/items and call/result IDs
  through real adapter serialization. Require native successful completion before
  effects; test failed/incomplete/truncated streams with complete-looking calls.
- **Durable continuation:** the composed SQLite whole-history test envelope now
  restores exposure/results without tool replay and blocks continuation when an
  injected precommit failure leaves an unresolved effect. Qualify the actual
  session/record owner; in-process reload is not process-kill recovery or production
  streaming evidence.
- **Revocation/reset:** [MCP SDK tests](mcp-qualification.md) exercise local current-
  authority checks with immutable exposure and fake stdio peers; production catalog
  freshness and concurrent changes remain unqualified. Define provider/model-switch
  and compaction reset boundaries without late-wrapper insertion or blind retries.
- **Still open:** discovery source/trigger, definition lifecycle, concrete adapter
  choices, permission/approval mechanics, uncertain-call resolution and persistence
  details. Native/wrapper cache economics require real-provider evidence; no live
  account access or billable test is authorized by this document.

## Evidence anchors

- [Execution design](execution-design.md): owner fallback wording, provisional loop
  and composed adapter/guard/SQLite test-envelope evidence; actual session-store
  integration and live-provider gates remain open.
- [MCP qualification](mcp-qualification.md): actual SDK, fake stdio peers and explicit
  2025 handshake—not default 2026 negotiation/fallback or model reliability.
- [Session design](session-design.md): direct request-only client composition and
  durable/live/client ownership; class and file layout approval limits.
- [Model-interface research](model-interface-research.md): pinned MEAI 10.11.0,
  OpenAI .NET 2.14.0, Anthropic 13.4.0 and OllamaSharp 5.5.0 findings. Research pins,
  not selected package versions.
- [Provider qualification](provider-qualification.md): route-specific restrictions
  and authentication/entitlement limits; [cache research](cache-research.md):
  documented prefix behaviour, not Urd cache-hit measurements.
