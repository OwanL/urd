# Model-interface research: Microsoft.Extensions.AI for Urd

> Research date 2026-11-03. Scope: MEAI as a **shared model interface and optional
> tool loop** for Urd's C#/.NET backend — what it owns, what escapes it must have,
> and how it interacts with Urd's cache-preserving context direction and dynamic
> tool discovery. This document selects no implementation and selects no discovery
> design. It complements, and does not edit, the primary docs.

## Status and evidence classes

Bounded read-only research. **No live model requests, accounts, credentials,
machine changes or repository code changes** were involved. Evidence classes used
below:

- **source-supported** — inspected in pinned package source (version+commit stated).
- **offline-tested** — additionally exercised in a small offline fixture (JSON only,
  no network), artifacts in `%TEMP%\urd-meai-roundtrip\`.
- **provider-doc-supported** — current official provider documentation; not version-
  pinned, recheck before implementation.
- **not provider-tested** — nothing here has run against a real provider. Urd's
  existing fake-model probes (GO-DOTNET-DISCOVERY) likewise remain evidence of
  API feasibility, not provider qualification.

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
to adopt the interface but defer/compare the loop, not reasons to avoid Abstractions.

## 2. Provider adapter fidelity: what is mapped, what is dropped, what escapes exist

Per-content/per-message `RawRepresentation` and `AdditionalProperties` exist through
the MEAI surface, and all inspected adapters actually populate them (source-supported).
Key per-adapter findings:

| Concern | OpenAI Chat Completions | OpenAI Responses (experimental) | Anthropic 13.4.0 | OllamaSharp 5.5.0 |
| --- | --- | --- | --- | --- |
| Reasoning surface | Non-standard `reasoning_content` (DeepSeek/vLLM-style) only | `ReasoningResponseItem`; `EncryptedContent` → `ProtectedData`; item id in `AdditionalProperties["reasoningItemId"]` | `ThinkingBlock` → `TextReasoningContent{ProtectedData=signature}`; redacted thinking preserved | `Message.Thinking` → `TextReasoningContent` |
| Reasoning replay | **Dropped** (no API slot) | Replayed incl. encrypted/id for store=false replay | Replayed (thinking/signature, redacted) | **Dropped** (no Ollama input slot) |
| Tool identity round-trip | CallId/Name/args preserved | preserved | preserved | **Call ids invented per replay** (Ollama has none) |
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
  Source-supported; **provider qualification pending** (no live calls).
- **Copilot**: no adapter exists anywhere in MEAI/OpenAI/Anthropic/OllamaSharp, and
  there is no public raw inference contract; Urd would own a thin custom transport
  (device-flow + token exchange → bearer). Source-supported absence.
- **Claude**: API-key only by owner decision (unchanged); adapter covers key access.
- **Ollama**: local-model tool calling depends on the model; tool call ids are
  synthetic and should not be relied on across turns.

## 3. Dynamic tool discovery and stable prefixes

### Provider-native discovery exists; the plain tools list is the portable path

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
  when round-tripping.
- **Claude** ([mid-conversation system messages and tool changes](https://platform.claude.com/docs/en/build-with-claude/mid-conversation-system-messages),
  provider-doc-supported): mid-conversation `role:"system"` messages are **stable**
  (no beta header). Mid-conversation tool changes need beta
  **`inline-tools-2026-09-15`**: `tools` is declared up front and never changes;
  `tool_addition`/`tool_removal` blocks (by `tool_reference` **or, with that beta,
  by value** via `{"type":"tool_definition","definition":…}` for tools unknown at
  start) live in mid-conversation system-message content; at least one non-deferred
  initial tool avoids a one-time full cache miss; placement restrictions apply; cache
  order is `tools` → `system` → `messages`.
- **Ollama/others**: no equivalent dynamic-discovery API; the only path is updating
  the ordinary tools list (cache consequences unmeasured/undocumented — flagged).

So the tradeoff is structural: **updating the ordinary tools list everywhere** is
portable and simple but rewrites the top-level tool block — documented cache-invalidating
prefix on OpenAI and Anthropic; **provider-native append mechanisms** preserve the
cached prefix but are bespoke per provider, partially beta, and absent on others.
Per owner steering: no wrapper-vs-native overhead claims are made — **neither is
measured** here; and nothing below depends on choosing between them yet.

### What the pinned SDK/adapters can and cannot carry today

- **OpenAI (offline-tested + source-supported):** openai-dotnet 2.14.0 recognizes
  `tool_search`/`tool_search_call`/`tool_search_output` only as *internal* kinds with
  **no typed public classes**, and has **no `additional_tools` support at all**
  (absent from the spec and source). However, MEAI's Responses adapter stashes
  unknown ResponseItems as `AIContent { RawRepresentation = item }` and replays them
  verbatim at position, and the offline fixture confirms unknown item kinds —
  including an `additional_tools` item — deserialize, preserve their type and payload,
  reserialize **byte-identically**, and re-parse via the SDK's public
  `ModelReaderWriter` path. So raw-carried OpenAI native discovery blocks are
  feasible **today** through `RawRepresentation`, without a wrapper — but they are a
  raw/undocumented-in-SDK escape hatch, Urd constructs and validates the JSON itself,
  and the adapter class is `[Experimental]`.
  Fixture artifacts: `%TEMP%\urd-meai-roundtrip\` (`Program.cs`, results in run log).
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
- **Persistence caveat:** MEAI's `RawRepresentation` is `[JsonIgnore]`; the
  Abstractions `ChatMessage` JSON surface does not preserve raw provider blobs. If
  Urd persists model-visible items for restart continuation, raw provider items must
  be reconstructed from Urd's own records (Urd owns storage design regardless).
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
   native mechanisms needs the raw escape hatch (OpenAI, works) or a custom adapter
   (Claude). This layer is MEAI's, fixable by adapter choice, not a provider fact.
3. **Harness dispatch (Pie's choice, not forced):** the fixed `invoke_tool({name})
   wrapper and append-only disclosure descriptors are Pie's design for permission
   ceilings, recovery and audit — one execution pipeline, two identities. It is how
   Pie avoided *needing* per-provider native discovery at all. It is not required by
   provider APIs, MEAI, or Urd's cache direction; a wrapper overhead concern or a
   native-discovery benefit is **not measured** by this research. Urd can keep native
   tool execution common while letting adapters handle exposure — inline native
   addition where supported, or ordinary tools-list updates elsewhere with an
   explicit, measured cache tradeoff.

Pie's IMMUTABLE-MODEL-CONTEXT plan (reference: `C:/dev/repos/pie/docs/plans/IMMUTABLE-MODEL-CONTEXT.md`,
commit `cc82abb0…`; source-supported, offline-tested, provider-cache measurements
unqualified) remains the working design path Urd's architecture references, and —
notably — it achieves prefix stability *without* any provider-native discovery beta,
because its `tools` array never changes mid-conversation.

## 4. Recommended decision (for owner decision; research recommendation, not a selection)

Among the three candidate shapes the owner framed (adopt abstractions only / adopt
abstractions plus the optional loop / custom alternative), this research recommends
**the abstractions-only shape as the working direction for owner consideration**:
adopt MEAI Abstractions (provider adapters behind `IChatClient`) as the shared model
interface, treat `Microsoft.Extensions.AI` middleware as strictly optional, and keep
loop ownership open with `FunctionInvokingChatClient` as one candidate to compare
against a Urd-owned loop on Urd's actual behaviors (per-tool admission, steering,
durable history, prefix stability). This is the option that best answers the owner's
"too much logic" worry: the interface is small and loop-free; adopting the
Abstractions does not lock in the loop or session state ownership, and every provider
concern inspected has a documented escape hatch — though several need Urd-side care
(plan-route flags via raw options, Anthropic sampling params, MaxTokens default,
silent content drops, synthetic Ollama ids). Urd's architecture record leaves
model interfaces and loop ownership open; this recommendation is research input, not
a recorded decision, and it has no implementation-selection standing until the owner
aligns and the qualification gates below produce live evidence.

**Do not build Urd's discovery design on provider-native beta features.** Keep the
portable path — updating the ordinary tools list with stable definitions — as the
common route, treat provider-native appends (OpenAI additional_tools/tool_search;
Claude inline-tools beta) as optional per-adapter cache-preservation capabilities
with an explicit cost/benefit to be measured, and never let a shared interface force
a wrapper or obscure actual request changes. If Claude-grade cache preservation on
discovery proves valuable before a custom adapter, that is the specific trigger for
a small Urd-owned `IChatClient` adapter around `IAnthropicClient`; otherwise accept
documented cache misses on tools-list updates until measured.

**Concrete Urd blockers / qualification gates (next steps, none live-tested yet):**

1. Scripted local HTTP/SSE adapter tests (offline, fake transport): OpenAI Responses
   route — store:false + include encrypted reasoning round-trip, tool_search /
   additional_tools raw items end-to-end through the adapter; Anthropic —
   mid-conversation system messages, byte-stable raw tools, schema-narrowing impact.
   *Offline-tested today: only the SDK unknown-item JSON round-trip fixture.*
2. Qualify the ChatGPT-plan Responses contract live (registration, entitlement,
   plan-route flags, streaming failure semantics) — requires owner-approved account
   use; distinct from and not covered here.
3. Measure the tools-list-update vs native-append cache tradeoff per provider
   (including whether Ollama-style local servers care) before selecting a discovery
   design; provider-doc claims alone do not establish Urd's savings.
4. Copilot raw transport remains its own qualification gap (no public contract).
5. If the MEAI loop is compared: audit last-iteration tool stripping, iteration cap
   behavior, approval handshake and cancellation against Urd's admission/steering
   requirements; a mixed pipeline (other middleware) can also alter options mid-call
   and must be within Urd's prefix-freeze control.

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