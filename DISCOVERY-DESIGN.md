# Dynamic tool discovery: proposed design comparison

**Status: design proposal only (2026-11-03).** The owner-approved constraints are C#/.NET with ASP.NET Core/Kestrel; preserve AI-based catalog pruning and protected core tools; prefer native tool calls over a generic invocation wrapper; investigate provider-specific cache-preserving exposure for OpenAI/Anthropic while retaining local and other providers. This report proposes one flow, but selects no model library, loop, discovery protocol, fallback policy, or storage engine.

## Preferred proposed flow

Use one Urd-level lifecycle with provider-specific *exposure*, not provider-specific tool execution:

1. **Select from the live catalog.** Keep the existing AI-based pruner. Exclude protected core/pinned entries from its candidate set; discovery complements pruning. A selected entry carries its complete definition and source identity. This does not prescribe MCP or another discovery protocol.
2. **Expose the definition once.** Add a versioned, ordered snapshot of the exact definition Urd intends the model to see. Prefer a provider's native append mechanism where supported and worth its extra code; otherwise send the updated ordinary tool list. Pin serialization/order. Do not mutate an earlier snapshot to reflect a later schema or permission change.
3. **Invoke natively.** The model returns a named function/tool call with its provider call ID and arguments. Route that call to Urd's current executable registry and apply current permission/admission rules; execute through the same tool path regardless of how its definition was exposed. Return the result under the matching call ID and append it to model-visible history. Do not route calls through a generic `invoke_tool({name})` wrapper.
4. **Continue from the saved model view after restart.** Reconstruct the ordered exposure and call/result history, including any provider-specific items. Separately rebuild the live registry and permissions. Historical exposure is context, never executable authority. Never automatically repeat a completed or uncertain side effect to repair a transcript.

### Responsibility sketch (proposed, not a class layout)

- **Catalog/pruner:** keeps protected core tools and chooses dynamic candidates.
- **Exposure path:** snapshots definitions/order and emits a provider-native append or ordinary tool-list update.
- **Model/tool cycle:** obtains calls, resolves them against the current registry and policy, invokes once, and records matching results. Whether Urd or an optional library owns the loop remains open.
- **Continuation/recovery:** rebuilds model-visible context and marks orphaned calls for resolution; it does not reactivate tools or replay effects.

## Exposure comparison

| Route | Proposed exposure | What is established / cost | Fallback or limit |
|---|---|---|---|
| **OpenAI Responses** | Prefer appending client-executable definitions as `additional_tools` items at their original positions, if qualifying this route. `tool_search` is a distinct provider-managed discovery option, not proof of the same Urd catalog → client tool invocation flow. | OpenAI docs describe append-style additions as prefix-preserving. OpenAI .NET 2.14.0 has no public typed `additional_tools`/tool-search item models. MEAI Responses 10.11.0 can preserve unknown response items through `RawRepresentation` and replay them at position; Urd would construct/validate raw request items. An Abstractions `HostedToolSearchTool` and adapter mapping to `tool_search` exist, but do not establish end-to-end client execution of Urd-discovered tools. The existing offline JSON fixture proves serialization only—not the MEAI adapter round trip or provider behavior. | Ordinary `tools` update is the simpler path, with documented cache-prefix cost. This native path is an escape hatch, not a clean typed SDK contract. Responses adapter is experimental. |
| **Anthropic / Claude** | If its cache benefit justifies the cost, append `tool_addition`/`tool_definition` blocks in a mid-conversation system message, with the required inline-tools beta. | Provider docs document the mechanism, but it is beta/model/placement constrained and requires an initial non-deferred tool for the described cache behavior. Anthropic SDK 13.4.0 has typed beta models. Its `AsIChatClient` adapter preserves mid-conversation system messages but its content conversion does not carry these beta blocks; a small Urd-owned adapter around the official client would be needed for this path. Raw definitions are needed if byte-stable schemas matter. | Ordinary `tools` update avoids custom beta transport but changes the hosted prefix. Do not mistake support for ordinary system text in `AsIChatClient` for support of inline tool blocks. |
| **OllamaSharp / local models** | Use the ordinary function-tool list when the specific server/model supports tool calling. | OllamaSharp 5.5.0 maps ordinary function declarations; the examined route has no dynamic append equivalent. Cache impact depends on the model template (some do not render tools); identical prefixes do not establish tool-call support. Pin definition serialization, and qualify IDs/round trips: OllamaSharp synthesizes call IDs on replay. | Ordinary-list update is the practical fallback, but may re-render an early prefix or be unsupported by a model. Test per model/server; do not generalize this finding to every local or OpenAI-compatible provider. |
| **Other adapters/providers** | Use their native append only after verifying the exact API and adapter can preserve it. | No blanket support claim is justified by the examined sources. | Ordinary tool-list update only where the adapter and model support it; otherwise dynamic tool use is unavailable on that route until qualified. |

A **stable generic wrapper** can keep a fixed native tool list and append descriptions as messages, but it is not the preferred flow: it adds an execution indirection the owner dislikes, and the prompt-token/maintenance tradeoff is unmeasured. Keep it as a consciously chosen alternative, not a hidden universal fallback.

## Minimum facts to restore

These are logical facts, not a proposed schema or storage format:

- **Exposure history:** stable tool/source identity; definition version (including name, description and schema as exposed); exact order and model-context position; route used; and the provider-specific block/item or ordinary tool-set snapshot required to reconstruct what the model saw. Preserve raw provider material in Urd-owned records where an adapter's representation is not durable.
- **Invocation/result history:** provider call ID (plus a Urd ID if needed), referenced definition version, tool name, arguments, sequence, and the exact result/error supplied back to the model. Preserve the original IDs and event order; do not synthesize a replacement identity that breaks call/result pairing.
- **Execution status:** enough to distinguish no recorded dispatch, a completed invocation with result, and a call whose effect is uncertain. An exposed definition or recorded historical call does not imply that its implementation is currently installed or authorized.
- **Live execution state, separate from history:** rebuild current implementation bindings and current permissions from live configuration/registries. Resolve and authorize each new invocation against that current state. If a definition changed or permission was revoked, preserve old model-visible history but block incompatible/stale execution; do not silently bind an old call to a new schema.

After restart, restore recorded results without rerunning them. An orphaned call that may have reached an external tool is uncertain; hold it for an explicit resolution policy rather than automatic replay. The agreed recovery bar is coherent continuation, not exact token persistence or an elaborate replay engine (`ARCHITECTURE.md`, “Practical crash recovery”).

## `IChatClient`: adoption assessment, not selection

`IChatClient` is a small, history-free request/response seam; its Abstractions package does not require the optional function loop. Standard `ChatOptions.Tools` and function-call/result content fit the ordinary portable path and can leave Urd in control of native invocation. That is useful shared plumbing, not a discovery protocol.

The seam is **not clean for all native exposure**. OpenAI Responses can carry provider-specific items only through raw/experimental paths; the Anthropic 13.4.0 adapter drops the inline beta blocks, so preserving that feature requires provider-specific code (which could itself implement `IChatClient`). Urd must also own durable provider-item reconstruction because MEAI `RawRepresentation` is ignored by default JSON serialization. Thus the interface still plausibly saves ordinary request/tool mapping across supported adapters, but it does **not** erase the bespoke code needed for cache-preserving OpenAI/Claude discovery. If those native paths are mandatory across both providers, that LOC saving is materially smaller; if ordinary-list fallback is acceptable and native appends are optional optimizations, the observed gaps do not by themselves defeat the interface's value. No loop choice follows from this assessment.

## Tradeoffs and remaining decisions

- Native append can preserve an earlier rendered prefix; it does not guarantee cache hits or measured savings. Plain top-level tool changes are documented cache breakers for OpenAI/Anthropic; local cost depends on rendering. Urd's workload savings remain unmeasured (`CACHE-RESEARCH.md`).
- Native paths cost distinct raw/typed adapter code and qualification. OpenAI raw support is not a typed end-to-end SDK contract; Claude's inline-tools path requires a custom adapter unless the SDK surface changes. Standard tool-list update is simpler and more portable, but may lose cache reuse.
- Still open: discovery source/trigger and definition lifecycle; which provider/model routes merit native append; behavior when a route lacks dynamic tool support; permission/approval policy; schema-revocation and uncertain-call resolution; whether `IChatClient` or a loop library is adopted; and which minimal facts/how often are persisted. Cache economics need actual per-provider metrics before native-path costs can be justified.

## Focused acceptance scenarios

1. **Prune, expose, invoke:** a protected core tool remains available while the pruner selects a dynamic tool; expose it once, receive a native call, invoke the intended live tool and return the same call ID/result without a wrapper.
2. **Adapter fidelity:** offline request-sequence tests verify exact order/round-trip of OpenAI `additional_tools` raw items and Anthropic beta blocks/header where implemented; fail on silent drops or schema drift. Keep SDK JSON-fixture checks separate from adapter and live-provider claims.
3. **Restart continuity:** restore multiple exposures plus completed call/result pairs in order, reconstruct provider-only items, and verify no completed side effect runs again; current permissions/registry are checked independently.
4. **Uncertain call:** crash/failure after dispatch but before result recording leaves an unresolved call and causes no automatic repeat or fabricated success; explicit resolution is required before unsafe continuation.
5. **Fallback and revocation:** change a tool's schema/revoke permission and exercise ordinary-list fallback on a qualified local model; preserve the old historical definition, block stale/unauthorized invocation, verify the deliberate context/cache reset, and test actual tool-call support rather than template rendering alone.

## Source anchors

- Approved direction and constraints: `ARCHITECTURE.md` §§ “Keep AI-based tool and skill pruning…” and “Preserve model-visible prefixes…” (around lines 423–484); `README.md` “C# backend…” / “Open design work”.
- Cache behavior and limitations: `CACHE-RESEARCH.md` §§ documented behaviour, immutable transcripts, unknowns, recommendations.
- Interface and provider pins: `MODEL-INTERFACE-RESEARCH.md` §§ 1, 3, 4. Pins: MEAI 10.11.0 / dotnet/extensions `v10.11.0` (`25f07af`); OpenAI .NET 2.14.0 (`2e77b08`); Anthropic 13.4.0 (`6eae95e`); OllamaSharp 5.5.0 (`135abcc`). These are research pins, not selected dependencies.
- Fake-model tool-loop probes: `GO-DOTNET-DISCOVERY.md` § “Small experiments”: call/result identity passed; simulated save failure happened **after** the tool already ran and is not durability evidence.
