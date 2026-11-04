# Model-interface research: Microsoft.Extensions.AI for Urd

> Research date 2026-11-03. Scope: MEAI as a **shared model interface and optional
> tool loop** for Urd's C#/.NET backend — what it owns, what escapes it must have,
> and how it interacts with Urd's cache-preserving context direction and dynamic
> tool discovery. Pinned findings below retain their original evidence limits;
> direction and qualification status were integrated on 2026-11-04.

> **Current direction:** `Microsoft.Extensions.AI.Abstractions` and `IChatClient`
> are selected in [ARCHITECTURE.md](ARCHITECTURE.md). The owner accepts a configured
> request-only client directly for session execution and auxiliary operations,
> without a mandatory Urd facade ([Session Design](SESSION-DESIGN.md)). A narrow
> Urd-owned cycle is the provisional direction. Discovery uses qualified native
> appended definitions, otherwise a stable invocation wrapper present from the
> initial catalog for recovered tools. [Execution Design](EXECUTION-DESIGN.md)
> records the owner decisions and bounded fixture evidence; [Provider qualification](PROVIDER-QUALIFICATION.md)
> owns route limits. Neither live-provider nor durable/composed execution is qualified.

## Status and evidence classes

The original research was bounded and read-only. **No live model requests,
accounts, credentials, machine changes or repository code changes** were involved.
Later local execution evidence is linked, not claimed as part of that research.
Evidence classes used below:

- **source-supported** — inspected in pinned package source (version+commit stated).
- **offline-tested** — additionally exercised in a small offline fixture (JSON only,
  no network), artifacts in `%TEMP%\urd-meai-roundtrip\`.
- **provider-doc-supported** — current official provider documentation; not version-
  pinned, recheck before implementation.
- **not provider-tested** — nothing here has run against a real provider. Earlier
  fake-model probes and the newer matched loop/local HTTP/SSE + file-reload checks
  in [Execution Design](EXECUTION-DESIGN.md#what-was-actually-exercised) establish
  bounded mechanics, not provider acceptance, durable recovery or a composed
  native-adapter + guarded tool-loop qualification.

## Pins

| Package | Version | Source pin |
| --- | --- | --- |
| Microsoft.Extensions.AI / .Abstractions | 10.11.0 (current NuGet) | dotnet/extensions tag `v10.11.0`, commit `25f07afa51034e37b334210642d7dc5f191c142d` |
| Microsoft.Extensions.AI.OpenAI | 10.11.0 | same commit; depends on official OpenAI 2.14.0 (`2e77b08828145f658ec04e49aec87abb1543c553`) |
| Anthropic (official Claude SDK for C#, `AsIChatClient`) | 13.4.0 (current NuGet) | anthropics/anthropic-sdk-csharp commit `6eae95e4689477382c81bb5b35b6386ff5b3686d` (verified from package nuspec) |
| ModelContextProtocol (.Core) | 2.3.1 | csharp-sdk tag `v2.3.1`, commit `61420937cbef3eab895cafc8a89418811fb71f54` |
| OllamaSharp | 5.5.0 | `135abcc3e31ac6a1ec535ff4c891e11ed1328df4` |

## 1. Minimal Abstractions vs optional middleware/loop

**The interface layer is genuinely separable; the loop is genuinely optional.**
This is the direct answer to the owner's worry that MEAI "handles too much."

- `Microsoft.Extensions.AI.Abstractions` holds `IChatClient`, the message/option/
  response model and content types; its net10.0 build has **no package dependencies**
  (source-supported: nuspec + csproj). Provider libraries reference only this
  (source/official-docs: [which package to reference](https://learn.microsoft.com/en-us/dotnet/ai/microsoft-extensions-ai#which-package-to-reference)).
- `IChatClient` is **loop-free and history-free**: `GetResponseAsync`/
  `GetStreamingResponseAsync` take the caller-supplied message list; no stored
  conversation state. Caveat: the contract remarks explicitly permit implementations
  to mutate supplied arguments (e.g. options) — callers must not assume immutability
  (source-supported: [IChatClient.cs](https://github.com/dotnet/extensions/blob/v10.11.0/src/Libraries/Microsoft.Extensions.AI.Abstractions/ChatCompletion/IChatClient.cs#L12-L57)).
- `Microsoft.Extensions.AI` (non-Abstractions) adds middleware: `FunctionInvokingChatClient`,
  logging/OpenTelemetry/caching chat clients, reducers, `ChatClientBuilder`. There is
  **no reverse dependency** forcing the middleware package; Urd can use Abstractions +
  provider adapters and run its own model→tool→model loop over
  `FunctionCallContent`/`FunctionResultContent` (source-supported; also demonstrated
  offline in the earlier fake-model probes).

### FunctionInvokingChatClient: what it would own, and what it lacks

If Urd adopted the optional loop client, it owns the per-call loop with these
behaviors (all source-supported at v10.11.0, [FunctionInvokingChatClient.cs](https://github.com/dotnet/extensions/blob/v10.11.0/src/Libraries/Microsoft.Extensions.AI/ChatCompletion/FunctionInvokingChatClient.cs)):

- History: it copies the message list (shallow) into its own augmented history and
  returns aggregated response messages; it does **not** own or persist session history.
- Iteration cap: default `MaximumIterationsPerRequest = 40`; on reaching it, it makes
  one final request **with function declarations stripped** — so a tool-spamming model
  can generate up to 41 inner model calls per outer call, and the tool prefix changes
  mid-call in that corner case. MEAI freezes nothing; prefix discipline is the caller's.
- Tool execution: serial by default; `AllowConcurrentInvocation` = `Task.WhenAll`;
  cancellation is cooperative. No automatic retry; no per-tool admission/approval
  UI or policy engine (there is an `ApprovalRequiredAIFunction` handshake, but the
  caller must build the policy and answer the request).
- Interception: caller can wrap functions, set `FunctionInvoker`, insert a
  `DelegatingChatClient` inside the loop (the seam the earlier Urd offline probe used
  to gate the next model request), and use `FunctionInvocationContext.Terminate`.
  These are hooks, not a steering/persistence system.
- Retry/fallback: nothing general in MEAI (Polly-style resilience is external); there
  is an **experimental** `OrderedFailoverChatClient` (failover across clients on
  failure — not backoff/retry, and experimental) (source-supported,
  [ChatRouting](https://github.com/dotnet/extensions/tree/v10.11.0/src/Libraries/Microsoft.Extensions.AI/ChatRouting)).

**"Handles too much" assessment:** the Abstractions layer does not; the middleware
package does exactly one substantial orchestration thing (the function loop) and is
opt-in. The real risks for Urd are (a) subtle behaviors like last-iteration tool
stripping that Urd would have to audit rather than trust, and (b) DI/builder
conventions if Urd adopted a larger MEAI surface than it needs. Both are reasons
to keep the selected interface separate from the loop, not reasons to avoid
Abstractions. The later matched comparison and owner preference favour a narrow
Urd-owned cycle; see [current direction](#4-current-direction-and-remaining-qualification).

## 2. Provider adapter fidelity: what is mapped, what is dropped, what escapes exist

Per-content/per-message `RawRepresentation` and `AdditionalProperties` exist through
the MEAI surface, and all inspected adapters actually populate them (source-supported).
Key per-adapter findings:

| Concern | OpenAI Chat Completions | OpenAI Responses (experimental) | Anthropic 13.4.0 | OllamaSharp 5.5.0 |
| --- | --- | --- | --- | --- |
| Reasoning surface | Non-standard `reasoning_content` (DeepSeek/vLLM-style) only | `ReasoningResponseItem`; `EncryptedContent` → `ProtectedData`; item id in `AdditionalProperties["reasoningItemId"]` | `ThinkingBlock` → `TextReasoningContent{ProtectedData=signature}`; redacted thinking preserved | `Message.Thinking` → `TextReasoningContent` |
| Reasoning replay | **Dropped** (no API slot) | Replayed incl. encrypted/id for store=false replay | Replayed (thinking/signature, redacted) | **Dropped by the inspected adapter**; current Ollama guidance supports replaying `thinking` (see [correction](PROVIDER-QUALIFICATION.md#reuse-only-where-it-helps)) |
| Tool identity round-trip | CallId/Name/args preserved | CallId/Name/args preserved; streamed native function-item ID needs raw event capture (below) | preserved | **Call ids invented per replay** (Ollama has none) |
| Raw escape hatch | request Patch / policies | unknown ResponseItems → `AIContent{RawRepresentation=item}`, replayed **verbatim at position** | raw `ToolUnion` passthrough; per-block `RawRepresentation is ContentBlockParam` replay; `RawRepresentationFactory` → `MessageCreateParams` | `RawRepresentationFactory` → `ChatRequest` |
| Cache controls | n/a | caller-set | `anthropic:cache_control` AdditionalProperties helper; tool-def cache breakpoint; **not on thinking blocks** | none |
| Notable drops/defaults | reasoning effort via ChatOptions only | adapter never forces store/stream/include; caller sets plan-route flags | temperature/top_p/top_k/seed/penalties unmapped (raw params only); **MaxTokens defaults to 1024**; unknown content in messages silently dropped | unknown roles/content dropped; no PDF/audio-in; per-model tool support |

Key sources: [OpenAIChatClient.cs](https://github.com/dotnet/extensions/blob/v10.11.0/src/Libraries/Microsoft.Extensions.AI.OpenAI/OpenAIChatClient.cs),
[OpenAIResponsesChatClient.cs](https://github.com/dotnet/extensions/blob/v10.11.0/src/Libraries/Microsoft.Extensions.AI.OpenAI/OpenAIResponsesChatClient.cs)
(non-streaming default-case raw stash ~L288–318; verbatim replay ~L1418/1552),
[AnthropicClientExtensions.cs](https://github.com/anthropics/anthropic-sdk-csharp/blob/6eae95e4689477382c81bb5b35b6386ff5b3686d/src/Anthropic/AnthropicClientExtensions.cs),
[OllamaSharp AbstractionMapper](https://github.com/awaescher/OllamaSharp/blob/135abcc3e31ac6a1ec535ff4c891e11ed1328df4).

The primary per-request provider-settings mechanism is
`ChatOptions.RawRepresentationFactory` returning the provider-native options object;
adapters fill only unset fields (`??=`), so caller-set raw values win. Request-level
headers: Anthropic via `ClientOptions.ExtraHeaders` (client-wide) or
`MessageCreateParams.RawHeaderData` (per request); OpenAI via an experimental
`OpenAIRequestPolicies` pipeline-policy hook. There is **no MEAI-wide
beta-header/AdditionalProperties convention** (source-supported).

**Subscription routes:**

- **ChatGPT plan** → Responses API: constructible through MEAI — the OpenAI .NET SDK
  `ResponsesClient` accepts custom `Endpoint` + bearer `AuthenticationPolicy`, MEAI's
  Responses adapter always streams and lets the caller set `store:false`, `include`
  encrypted reasoning, function-only tools via raw options. The adapter does **not**
  force plan-route-required flags; Urd owns them via `RawRepresentationFactory`.
  The plan route requires namespaces or `additional_tools` for function exposure
  and forbids `tool_search`, including client-executed search. Source-supported
  plus newer local scripted adapter evidence; **provider qualification pending**
  (no live calls). [Provider qualification](PROVIDER-QUALIFICATION.md#first-route-exact-auth--request--tool-continuation)
  owns the current request restrictions.
- **Copilot**: no adapter exists anywhere in MEAI/OpenAI/Anthropic/OllamaSharp, and
  there is no public raw inference contract; Urd would own a thin custom transport
  (device-flow + token exchange → bearer). Source-supported absence.
- **Claude**: API-key only by owner decision (unchanged); adapter covers key access.
- **Ollama**: local-model tool calling depends on the model; tool call ids are
  synthetic and should not be relied on across turns.

## 3. Dynamic tool discovery and stable prefixes

### Provider-native discovery exists; the selected fallback is a stable wrapper

- **OpenAI Responses** ([tool-search guide](https://developers.openai.com/api/docs/guides/tools-tool-search),
  provider-doc-supported): `tool_search` tool with hosted or **client-executed**
  mode (gpt-5.4+); discovered definitions are injected **at the end of the context**
  via `tool_search_output` items explicitly to preserve the cache; changing the
  loaded tool set later breaks the cache from that point. Separately, an
  **`additional_tools`** developer-role input item makes tools available from a
  specific conversation point; the [prompt-caching guide](https://developers.openai.com/api/docs/guides/prompt-caching)
  lists top-level `tools` changes (names/descriptions/schemas/ordering) among
  cache-breaking settings and recommends append-style updates; `additional_tools`
  items cannot carry explicit cache breakpoints. Item position must be preserved
  when round-tripping. This describes the general API, **not ChatGPT-plan access**:
  that route forbids `tool_search`; its permitted appended-definition path is
  `additional_tools`.
- **Claude** ([mid-conversation system messages and tool changes](https://platform.claude.com/docs/en/build-with-claude/mid-conversation-system-messages),
  provider-doc-supported): mid-conversation `role:"system"` messages are **stable**
  (no beta header). Mid-conversation tool changes need beta
  **`inline-tools-2026-09-15`**: `tools` is declared up front and never changes;
  `tool_addition`/`tool_removal` blocks (by `tool_reference` **or, with that beta,
  by value** via `{"type":"tool_definition","definition":…}` for tools unknown at
  start) live in mid-conversation system-message content; at least one non-deferred
  initial tool avoids a one-time full cache miss; placement restrictions apply; cache
  order is `tools` → `system` → `messages`.
- **Ollama/others**: the inspected evidence does not qualify an equivalent
  appended-definition route through the selected provider/adapter combination.
  This is not a blanket claim that Ollama cannot support native tools or future
  discovery mechanisms. Ordinary tools-list changes have unmeasured cache effects
  here and are not Urd's selected recovery fallback.

The tradeoff is structural: ordinary tools-list updates rewrite the top-level
block — documented as cache-invalidating on OpenAI and Anthropic. Qualified native
appends avoid that rewrite but are provider/adapter-specific and sometimes beta.
Urd's current direction is **qualified native appends with no invocation wrapper,
otherwise a stable wrapper included from the initial catalog**, with recovered-tool
exposure appended to context. The fallback is not ordinary-list updates. Neither
wrapper overhead/reliability nor Urd cache savings is measured here.

### What the pinned SDK/adapters can and cannot carry today

- **OpenAI (offline-tested + source-supported):** openai-dotnet 2.14.0 recognizes
  `tool_search`/`tool_search_call`/`tool_search_output` only as *internal* kinds with
  **no typed public classes**, and has **no `additional_tools` support at all**
  (absent from the spec and source). However, MEAI's Responses adapter stashes
  unknown ResponseItems as `AIContent { RawRepresentation = item }` and replays them
  verbatim at position, and the offline fixture confirms unknown item kinds —
  including an `additional_tools` item — deserialize, preserve their type and payload,
  reserialize **byte-identically**, and re-parse via the SDK's public
  `ModelReaderWriter` path. This establishes SDK raw-item construction, not
  streaming/persistence fidelity or provider acceptance. The later local adapter
  fixture carried ordered `additional_tools` using an assistant-role MEAI carrier
  while preserving the native developer role; developer-role raw content dropped
  in its negative control. This is an experimental/raw escape hatch requiring
  owned reconstruction, not a qualified native discovery integration. Its
  `tool_search_output` was codec stress only, invalid as a ChatGPT-plan example.
  Original fixture artifacts: `%TEMP%\urd-meai-roundtrip\` (`Program.cs`, run log);
  newer evidence: [Execution Design](EXECUTION-DESIGN.md#real-adapter--local-httpsse--file-reload-seven-requests-passed).
- **Claude (source-supported):** Anthropic SDK **13.4.0** ships typed models for the
  beta (tool_addition/tool_definition, `AnthropicBeta.InlineTools2026_09_15`) only in
  its **Beta** namespace, plus a helper runner that explicitly does *not* add the
  beta header. The MEAI `AsIChatClient` adapter (non-beta surface) **does** emit
  mid-conversation `role:"system"` messages in place (fixed between SDK 11.0.0 and
  13.4.0 — the hoist finding applies to older versions), but its system-role path
  accepts only `TextContent` or a raw `TextBlockParam`; **tool_addition /
  tool_definition blocks are not expressible through the adapter** and would be
  silently dropped. Byte-stable raw tool definitions can be pinned via
  `ToolUnion` raw passthrough + `RawRepresentationFactory` (with per-request
  `RawHeaderData` for the beta header), so a **custom thin `IChatClient` adapter**
  around `IAnthropicClient` — Urd-owned, no wrapper tool — is what the
  Claude-beta cache-preserving path would need. MEAI/official-adapter composition
  alone cannot do the Claude part without dropping/rewriting blocks.
- **Schema fidelity caveat (both adapters):** declarations serialized from
  `AIFunctionDeclaration` are rebuilt per request; the Anthropic adapter narrows JSON
  schemas to `properties`+`required` (dropping `$defs` and other keywords) and
  re-serializes function-call arguments from a dictionary (byte form may drift).
  Pinning byte-stable tool definitions requires the raw passthrough, not ordinary
  declared tools.
- **Streaming identity and terminal caveats:** in the newer pinned Responses
  adapter fixture, `FunctionCallContent` preserved call ID/name/parsed arguments
  but lost the native function-item ID/raw representation. Capture
  `StreamingResponseOutputItemDoneUpdate.Item` before discarding/coalescing updates
  and retain/reconstruct the native item. Complete-looking calls can survive
  failed/incomplete/EOF streams with no generic finish reason or exception;
  explicit SSE errors can be `ErrorContent`. Require positive route-native terminal
  completion before dispatch, not merely successful enumeration or parsed arguments.
- **Persistence caveat:** MEAI's `RawRepresentation` is `[JsonIgnore]`; default
  `ChatMessage` JSON loses raw provider items. The newer fixture restored native
  IDs/payload/order through indexed reconstruction after file reload; encrypted
  reasoning and reasoning-item ID already survive ordinary JSON via `ProtectedData`
  and `AdditionalProperties`. This is not atomic SQLite storage or crash recovery.
  See [Execution Design's adapter evidence](EXECUTION-DESIGN.md#real-adapter--local-httpsse--file-reload-seven-requests-passed)
  for the checks and remaining composed-test gap; Urd's record owner must preserve
  the necessary native data.
- **MCP:** `McpClient.ListToolsAsync` returns `McpClientTool : AIFunction` carrying
  the full protocol definition (`ProtocolTool`), with a public constructor to reuse a
  captured definition without re-listing; no SDK-internal listing cache. Descriptors
  vs native appends are Urd's choice (source-supported,
  [McpClientTool.cs](https://github.com/modelcontextprotocol/csharp-sdk/blob/61420937cbef3eab895cafc8a89418811fb71f54/src/ModelContextProtocol.Core/Client/McpClientTool.cs)).

### Was Pie/Pi's wrapper the only cause of the prefix constraints?

No. Three distinct layers produce the constraint, and they differ in what they force:

1. **Provider API (real, external):** providers hash/serialize the top-level `tools`
   array early in the cached prefix (Claude: `tools` → `system` → `messages`; OpenAI
   documents `tools` changes as cache-breaking). *Any* harness that changes tools
   mid-conversation pays a cache cost unless it uses provider-native append
   mechanisms. This layer exists with or without Pi.
2. **SDK representation (MEAI-specific):** the adapters rebuild typed requests per
   call and do not model mid-conversation tool blocks (Claude beta), so using those
   native mechanisms needs the raw escape hatch (OpenAI, locally exercised with
   caveats above) or a custom adapter (Claude). This layer is MEAI's, fixable by
   adapter choice, not a provider fact.
3. **Harness dispatch (Pie's choice, not forced):** the fixed `invoke_tool({name})`
   wrapper and append-only disclosure descriptors are Pie's design for permission
   ceilings, recovery and audit — one execution pipeline, two identities. It is how
   Pie avoided *needing* per-provider native discovery at all. It is not required by
   provider APIs, MEAI, or Urd's cache direction; a wrapper overhead concern or a
   native-discovery benefit is **not measured** by this research. Urd can keep native
   tool execution common while letting adapters handle exposure — qualified native
   additions, otherwise a stable initial-catalog wrapper for recovered tools.
   Both routes resolve current permissions and implementations; historical
   exposure is not authority. The invocation wrapper is a model-visible tool,
   **not** the extra model-client facade the owner declined to require.

Pie's IMMUTABLE-MODEL-CONTEXT plan (reference: `C:/dev/repos/pie/docs/plans/IMMUTABLE-MODEL-CONTEXT.md`,
commit `cc82abb0…`; source-supported, offline-tested, provider-cache measurements
unqualified) remains the working design path Urd's architecture references, and —
notably — it achieves prefix stability *without* any provider-native discovery beta,
because its `tools` array never changes mid-conversation.

## 4. Current direction and remaining qualification

The original abstractions-only recommendation is now resolved: **MEAI Abstractions
and `IChatClient` are selected**. The owner accepts direct use of a configured,
request-only `IChatClient` by session execution and title/compaction/pruning
operations; no additional Urd client facade is mandatory. Request configuration
and provider fidelity do not themselves supply a tool loop or durable session.
[Session Design](SESSION-DESIGN.md) owns that integration, not this research.

**A narrow Urd-owned cycle is the provisional direction**, after the owner rejected
adding the optional function-loop dependency for about **20 lines saved in the
incomplete disposable comparison**. This is not a production implementation or
maintenance-cost estimate, nor evidence that `FunctionInvokingChatClient` cannot
meet Urd's needs. [Execution Design](EXECUTION-DESIGN.md#recommendation-and-owner-decisions)
records the comparison and decision; reconsider if real implementation recreates
substantial library machinery.

**Discovery direction supersedes this research's ordinary-tools-list fallback:**
use qualified provider **and adapter** native appended definitions and omit the
invocation wrapper from the initial catalog in that case. Otherwise include a
stable invocation wrapper **from the start** for recovered tools, disclosing their
definitions through appended context rather than changing the ordinary list.
Retain AI catalog pruning, excluding protected core/pinned tools and skills before
selection, and current implementation/permission checks on both routes. Initially
selected native tools need not all become wrapper calls. This model-visible
invocation tool is unrelated to a model-client facade. Neither a universal discovery
framework nor a Claude beta adapter is approved by this direction.

**Evidence and remaining gates:**

- [Execution Design](EXECUTION-DESIGN.md#what-was-actually-exercised) now records
  matched fake-loop checks and scripted local HTTP/SSE + file-reload evidence for
  the real pinned Responses adapter. The old "SDK JSON only" status is superseded;
  fixture details and reproduction belong there, not duplicated here.
- A composed native adapter + terminal guard + Urd cycle + harmless tool test
  through the selected SQLite record owner remains outstanding. Separate fixture
  successes do not establish composed correctness, atomic persistence or recovery.
- Live ChatGPT-plan registration/entitlement/inference/tool continuation still
  requires explicit owner approval. `tool_search` is not permitted on that route;
  additional-tools fixture acceptance is not provider acceptance.
- Claude inline-tool fidelity and Ollama server/model/adapter behavior remain
  unqualified; Copilot still has a separate authorization/support-contract gap.
  [Provider qualification](PROVIDER-QUALIFICATION.md) owns those route gates.
- Wrapper reliability/cost, native provider acceptance and actual cache savings
  remain unmeasured. Ordinary-list updates are an alternative, not the selected
  fallback, and no automatic retry/fallback or tool replay is authorized.

No API, class layout or production scaffold is selected by this integration.

## Sources

Pinned source (all fetched 2026-11-03):

- dotnet/extensions tag [v10.11.0](https://github.com/dotnet/extensions/tree/v10.11.0) (commit `25f07afa…`): Abstractions
  [IChatClient](https://github.com/dotnet/extensions/blob/v10.11.0/src/Libraries/Microsoft.Extensions.AI.Abstractions/ChatCompletion/IChatClient.cs),
  [AIContent \(STJ polymorphism, RawRepresentation \[JsonIgnore\]\)](https://github.com/dotnet/extensions/blob/v10.11.0/src/Libraries/Microsoft.Extensions.AI.Abstractions/Contents/AIContent.cs),
  [FunctionInvokingChatClient](https://github.com/dotnet/extensions/blob/v10.11.0/src/Libraries/Microsoft.Extensions.AI/ChatCompletion/FunctionInvokingChatClient.cs),
  [OrderedFailoverChatClient](https://github.com/dotnet/extensions/blob/v10.11.0/src/Libraries/Microsoft.Extensions.AI/ChatRouting/OrderedFailoverChatClient.cs),
  [OpenAIResponsesChatClient](https://github.com/dotnet/extensions/blob/v10.11.0/src/Libraries/Microsoft.Extensions.AI.OpenAI/OpenAIResponsesChatClient.cs),
  [OpenAIChatClient](https://github.com/dotnet/extensions/blob/v10.11.0/src/Libraries/Microsoft.Extensions.AI.OpenAI/OpenAIChatClient.cs).
- openai/openai-dotnet commit [`2e77b088…`](https://github.com/openai/openai-dotnet) (2.14.0):
  [ResponseItem kind registry](https://github.com/openai/openai-dotnet/blob/2e77b08828145f658ec04e49aec87abb1543c553/OpenAI.Responses/src/Generated/Models/ResponseItemKind.cs)
  (tool_search kinds internal), [unknown-item deserialization](https://github.com/openai/openai-dotnet/blob/2e77b08828145f658ec04e49aec87abb1543c553/OpenAI.Responses/src/Generated/Models/ResponseItem.Serialization.cs)
  (`InternalUnknownItemResource` raw carry), [CreateResponseOptions](https://github.com/openai/openai-dotnet/blob/2e77b08828145f658ec04e49aec87abb1543c553/OpenAI.Responses/src/Custom/CreateResponseOptions.cs).
- anthropics/anthropic-sdk-csharp commit [`6eae95e4…`](https://github.com/anthropics/anthropic-sdk-csharp) (13.4.0):
  [AnthropicClientExtensions \(adapter\)](https://github.com/anthropics/anthropic-sdk-csharp/blob/6eae95e4689477382c81bb5b35b6386ff5b3686d/src/Anthropic/AnthropicClientExtensions.cs)
  (mid-conversation system ~L1044–1050; system-branch block filtering ~L1020–1068),
  [BetaRequestToolAdditionBlock](https://github.com/anthropics/anthropic-sdk-csharp/blob/6eae95e4689477382c81bb5b35b6386ff5b3686d/src/Anthropic/Models/Beta/Messages/BetaRequestToolAdditionBlock.cs),
  [AnthropicBeta enum \(inline-tools 2026-09-15\)](https://github.com/anthropics/anthropic-sdk-csharp/blob/6eae95e4689477382c81bb5b35b6386ff5b3686d/src/Anthropic/Models/Beta/AnthropicBeta.cs).
- modelcontextprotocol/csharp-sdk tag [v2.3.1](https://github.com/modelcontextprotocol/csharp-sdk/tree/v2.3.1):
  [McpClientTool](https://github.com/modelcontextprotocol/csharp-sdk/blob/61420937cbef3eab895cafc8a89418811fb71f54/src/ModelContextProtocol.Core/Client/McpClientTool.cs),
  [ListToolsAsync](https://github.com/modelcontextprotocol/csharp-sdk/blob/61420937cbef3eab895cafc8a89418811fb71f54/src/ModelContextProtocol.Core/Client/McpClient.Methods.cs).
- awaescher/OllamaSharp commit [`135abcc3…`](https://github.com/awaescher/OllamaSharp) (5.5.0): AbstractionMapper.
- NuGet: [Microsoft.Extensions.AI 10.11.0](https://www.nuget.org/packages/Microsoft.Extensions.AI/10.11.0),
  [Anthropic 13.4.0](https://www.nuget.org/packages/Anthropic/13.4.0) (nuspec repository/commit fields — source of pins).
- Provider docs (provider-doc-supported, live, not version-pinned):
  [OpenAI tool search](https://developers.openai.com/api/docs/guides/tools-tool-search),
  [OpenAI prompt caching](https://developers.openai.com/api/docs/guides/prompt-caching),
  [Anthropic mid-conversation system messages and tool changes](https://platform.claude.com/docs/en/build-with-claude/mid-conversation-system-messages).
- Pie reference: `docs/plans/IMMUTABLE-MODEL-CONTEXT.md` at commit
  `cc82abb0f317f31341d3a6f12aa1545f6be1fbea` (in the Pie repository).
- Urd context: [ARCHITECTURE.md](ARCHITECTURE.md) (model-call composition alignment;
  preserve-model-visible-prefixes direction; provider-access decision),
  [GO-DOTNET-DISCOVERY.md](GO-DOTNET-DISCOVERY.md) (prior offline MEAI loop probes),
  [PROVIDER-RESEARCH.md](PROVIDER-RESEARCH.md) (subscription route seams).
- Offline fixture artifacts: `%TEMP%\urd-meai-roundtrip\` (program + csproj); evidence
  files cached by this research under `%TEMP%\urd-mcai-research\`.