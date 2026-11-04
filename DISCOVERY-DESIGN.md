# Dynamic tool discovery: direction and qualification

**Initial comparison: 2026-11-03; owner alignment integrated 2026-11-04.**
[ARCHITECTURE.md](ARCHITECTURE.md) owns decisions. `IChatClient` and shared SQLite
are selected; a narrow Urd-owned model/tool cycle is provisional. The owner now
prefers **qualified native appended definitions, otherwise a stable invocation
wrapper present from the initial catalog**. This supersedes the earlier proposal
to use ordinary tools-list updates as the portable fallback. It does not select
concrete provider adapters, discovery triggers, class APIs or a production schema.

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
[Session design](SESSION-DESIGN.md) composes request-only `IChatClient` directly
for execution and auxiliary operations. Both exposure routes share execution;
no per-provider session engine or general discovery framework is selected.

## Route evidence and gaps

Pins and detailed source findings remain in
[model-interface research](MODEL-INTERFACE-RESEARCH.md). These findings qualify
specific mechanics, not live-provider acceptance or cache savings.

| Route | Native path and established evidence | Fallback / remaining qualification |
| --- | --- | --- |
| **OpenAI Responses** | `additional_tools` can represent appended client-executable definitions. OpenAI .NET 2.14.0 lacks public typed support; MEAI 10.11.0 uses raw unknown items. The newer [execution fixture](EXECUTION-DESIGN.md) exercised the real adapter against scripted local HTTP/SSE and file reload, preserving ordered exposure with explicit native reconstruction. | Responses adapter remains experimental. Streaming native function-item IDs and terminal status need explicit handling; default message JSON loses raw representation. Native acceptance and the composed adapter + guarded loop + SQLite path remain unqualified. Use the initial wrapper on routes without qualified native support. |
| **Anthropic / Claude** | Provider documents inline `tool_addition`/`tool_definition` beta blocks, subject to model/placement restrictions. SDK 13.4.0 has typed beta models, but its ordinary `AsIChatClient` adapter drops those blocks despite preserving mid-conversation system text. | Native append needs separate adapter qualification; no Claude beta adapter is approved here. Stable wrapper is the selected fallback, not ordinary-list updates. |
| **OllamaSharp / local models** | The inspected 5.5.0 adapter maps ordinary function declarations; no native append equivalent was established for that route. Actual tool support, thinking replay and call identity require server/model/adapter testing. | Include the stable wrapper initially for recovered tools where function calling works. Wrapper reliability is untested; do not generalize this adapter finding to every local/OpenAI-compatible server. |
| **Other routes** | Native appends require exact provider+adapter evidence; none is implied by implementing `IChatClient`. | Initial-wrapper fallback also needs tool/model qualification; unsupported tool calling is not repaired by a wrapper. |

**Route distinction:** OpenAI `tool_search` is a separate hosted/client-executed
facility on eligible routes, not equivalent to Urd's client-controlled appended
exposure. The documented **ChatGPT-plan route forbids `tool_search`**, while
allowing `additional_tools`; see [provider qualification](PROVIDER-QUALIFICATION.md).
Execution's synthetic `tool_search_output` item tests an unknown-item codec only,
not a valid plan request. The original SDK JSON-only fixture was weaker than the
later adapter fixture; neither is a real-provider round trip.

Ordinary tools-list updates remain a consciously available alternative, **not the
selected fallback**. The initial-wrapper rule favours prefix stability, not measured
savings. Do not insert a wrapper late, silently switch exposure after a rejection,
or replay tools as a fallback. Provider-switch/reset behaviour still needs design.

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
- **Durable continuation:** compose the guarded adapter and Urd cycle with SQLite;
  restore exposure/results without side-effect replay. The existing file-reload
  fixture and simulated recording failure are not SQLite crash evidence.
- **Revocation/reset:** preserve historical definitions while rejecting stale
  execution. Define provider/model-switch and compaction reset boundaries without
  accidental late-wrapper insertion or blind retries.
- **Still open:** discovery source/trigger, definition lifecycle, concrete adapter
  choices, permission/approval mechanics, uncertain-call resolution and persistence
  details. Native/wrapper cache economics require real-provider evidence; no live
  account access or billable test is authorized by this document.

## Evidence anchors

- [Execution design](EXECUTION-DESIGN.md): exact owner fallback wording, provisional
  loop direction, matched fake-loop and scripted HTTP/SSE/file-reload evidence,
  plus the outstanding composed and live-provider gates.
- [Session design](SESSION-DESIGN.md): direct request-only client composition and
  durable/live/client ownership; class and file layout approval limits.
- [Model-interface research](MODEL-INTERFACE-RESEARCH.md): pinned MEAI 10.11.0,
  OpenAI .NET 2.14.0, Anthropic 13.4.0 and OllamaSharp 5.5.0 findings. Research pins,
  not selected package versions.
- [Provider qualification](PROVIDER-QUALIFICATION.md): route-specific restrictions
  and authentication/entitlement limits; [cache research](CACHE-RESEARCH.md):
  documented prefix behaviour, not Urd cache-hit measurements.
