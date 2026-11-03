# Prompt-cache research: dynamic tool discovery and the actual provider cache semantics

> Research date 2026-11-03. Purpose: establish documented, source-backed facts about
> provider prompt caches as Urd designs dynamic tool discovery — directly testing
> the blanket claim that "adding tools at the beginning completely breaks cache."
> This document selects no discovery protocol and edits no other Urd doc.
> Bounded read-only investigation: no live provider requests, accounts, credentials,
> benchmark or machine changes.

## Evidence classes

- **provider-doc-supported** — current official provider documentation, quoted
  verbatim and re-verified verbatim by an independent check on 2026-11-03; live
  pages, not version-pinned, recheck before implementation.
- **source-supported** — inspected in pinned source (Ollama, llama.cpp; commit stated).
- **issue-reported** — a specific provider/stack issue report; evidence that a
  behaviour can occur, not a general guarantee.
- **inference** — reasoned from documented mechanics; flagged inline.
- Not used anywhere: JSON request field order as evidence about rendered prompt order.

## The direct answer to the owner's challenge

The blanket claim is **imprecise but directionally correct for the hosted providers,
and wrong for local stacks as a universal rule**. What providers actually document
nowhere says "adding tools breaks cache because they come first." The real mechanism
is uniform and prefix-based:

- The provider caches KV state for the **longest matching rendered prefix**. A
  change at some point invalidates reuse **from that point onward**, not the cache
  entry itself ("Changing a request does not necessarily discard an existing cache
  entry. What matters is whether a subsequent request has the same prefix" —
  [OpenAI prompt caching](https://developers.openai.com/api/docs/guides/prompt-caching)).
- A tools change is effectively **whole-conversation** only because tools render
  near the very front of the rendered context (OpenAI: cached segment diagram
  `Hidden system → Tools → Developer → Context history`; Anthropic: "Cache prefixes
  are created in the following order: `tools`, `system`, then `messages`"), and
  Anthropic additionally spells out the consequence: "Modifying tool definitions
  (names, descriptions, parameters) invalidates the entire cache."
  [Anthropic prompt caching](https://platform.claude.com/docs/en/build-with-claude/prompt-caching).
- On local stacks the rendered prefix depends on the **model template**. Ollama's
  cache is longest-common-prefix over rendered prompt tokens; a template that does
  not render tools at all (e.g. Ollama's `llama3-instruct.gotmpl` has no `.Tools`
  reference) may leave rendered prompt tokens unchanged when tools are added, while
  a Qwen-style template that emits tool definitions inside the early system block
  makes them costly (source-supported; [Ollama template](https://github.com/ollama/ollama/blob/9a3e8177767245acc62b735eba7d5d8a553606d0/template/llama3-instruct.gotmpl)).
  This says nothing about whether the request accepts or can use those tools; an
  unchanged prefix is not evidence of supported native tool discovery.

So: for OpenAI and Anthropic with the **plain top-level `tools` array**, adding,
removing or reordering a tool does invalidate essentially the whole cached prefix —
that part of the claim holds, and both providers list ordering among the cache breakers.
But it is a consequence of prefix mechanics and render position, not a rule "tools
at the beginning always break cache"; transcripts are not semantically immutable to
the provider, and both providers ship native mechanisms whose entire purpose is
appending tools mid-conversation **without** invalidating the prefix.

## Documented behaviour matrix

Every row quotes or cites current official docs. "Prefix" always means the rendered
model-visible prefix, never JSON field order.

| Event between requests | OpenAI (api/docs/guides/prompt-caching unless noted) | Anthropic (platform.claude.com/…/prompt-caching) | Ollama / llama-server (source-supported @ ollama `9a3e817`, llama.cpp `19e8a86`) |
| --- | --- | --- | --- |
| **Add a tool** (plain `tools` array) | Cache- breaker: `tools` row — "Changes tool names, descriptions, schemas, ordering"; diagnostics reason `tools_changed` = "Tools were added, removed, or reordered, or their descriptions, schemas, or configuration changed." | "Modifying tool definitions (names, descriptions, parameters) invalidates the entire cache"; diagnostics `tools_changed` includes added/removed/reordered. | Cost depends on model-template rendering; rendered tokens may be unchanged, partially different, or differ near the beginning. An unchanged rendering does not establish that tool use is supported. |
| **Remove a tool** | Same as above (`tools_changed` covers removed). | Same. | Same as add; only suffix after the first rendered difference is reprocessed. |
| **Reorder tools** | Explicit: "ordering" is in the cache-breaking row. | Explicit: "reordered" in `tools_changed`. | Same prefix logic; any rendered-text change at the tools' position breaks from there. |
| **Append new messages** (ordinary continuation) | Safe: "Append new messages rather than rewriting earlier turns." Implicit boundaries include earlier message endings (up to 20). | Safe: longest previously written prefix still matches; lookback window ~20 blocks, or more with explicit breakpoints. | Safe: longest common prefix grows. |
| **Change an earlier message** | Reuse lost **from that change onward**; earlier cached sub-prefixes may still hit ("Cache reuse requires the entire rendered prefix to match"). | Lost **from that change onward**; an unchanged, previously-written earlier breakpoint can still hit. Exact match: "Cache hits require 100 percent identical prompt segments." | Lost from the first differing token onward. |
| **Extend/edit a recent message** | Caveated: "Extending a message can prevent reuse of its cached prefix" (implicit message-end boundary disappears unless an explicit breakpoint was set there). | Appending blocks after the last cached prefix still reuses it; editing cached blocks loses from the edit. | Same token-prefix logic. |
| **Change system prompt** | System content is part of the rendered prefix → lost from that point. | Hierarchy level 2 → invalidates system and messages. | Depends on template placement; same rule from first difference. |
| **Change `tool_choice`** | Inference, not directly verified here: it may affect rendered instructions; `parallel_tool_calls` is a distinct setting and is not evidence about `tool_choice`. | Explicitly documented: tools and system caches preserved, **messages cache invalidated**. | Whether it renders into prompt text depends on template; otherwise largely undocumented. |
| **Compaction / summarising earlier history** | "Compaction can prevent reuse from the first changed token onward." | Diagnostics classify altered/reordered/removed earlier messages as `messages_changed`. | Token-prefix rule; reuse survives only up to the first changed token. |
| **Retry an identical request** | Identical prefix + eligible breakpoint may permit reuse; a cache hit is not guaranteed. Retry-of-failed-request specifically undocumented. | Prefix is cached "once the response starts"; retry after a started response may reuse it. Failed-before-output retries undocumented. | Identical rendered tokens may permit reuse while the runner/slot stays loaded; actual hit behavior depends on the server and cache state. |
| **Native mid-stream tool addition** | **Designed for cache preservation**: tool search "inject[s tools] at the end of the context window"; "changing the loaded tool set will break the model's cache from that point forward" — i.e. the earlier prefix survives. gpt-5.4+ (Responses). `additional_tools` developer items: position must be preserved; "additional_tools input items do not currently accept prompt_cache_breakpoint." ([tool-search guide](https://developers.openai.com/api/docs/guides/tools-tool-search)) | Mid-conversation `role:"system"` messages are **stable (no beta)**. Tool changes need beta `inline-tools-2026-09-15` (`tool_addition`/`tool_removal` blocks; by-value `tool_definition` for tools unknown at start); keep ≥1 non-deferred initial tool to avoid a one-time full miss; placement restrictions apply. By-reference variant: `mid-conversation-tool-changes-2026-07-01`. ([mid-conversation page](https://platform.claude.com/docs/en/build-with-claude/mid-conversation-system-messages)) | **None exists.** The only path is ordinary `tools`, with template-dependent cache cost. |

Cache mechanics background (all documented): OpenAI stores **KV tensors**, caching
is automatic for supported models, minimum ~1,024 visible tokens on new GPT models
(varies earlier, "varies with request settings, including tools"), TTLs range
in-memory minutes to explicit 12h (Responses) / 24h retention settings; Anthropic
requires exact prefix match, up to **4 explicit breakpoints** with `cache_control`
("ephemeral", 5m/1h TTL), plus one automatic breakpoint, writes at breakpoints,
reads walk back to the longest written prefix.

## Route and model differences worth recording

- **OpenAI Chat Completions vs Responses:** same prefix semantics; Responses adds a
  12h TTL option and OpenAI's own migration doc claims "improved cache utilization
  (40% to 80% improvement when compared to Chat Completions in internal tests)".
- **ChatGPT-plan route (Sign in with ChatGPT, `store:false`, `stream:true`):**
  no official statement ties `store` to prompt caching either way — **undocumented,
  must not be assumed** either direction. Nothing contradicts caching on this route.
- **Explicit breakpoints:** Responses API supports `prompt_cache_breakpoint` content
  blocks (≤4). The restriction is that top-level `instructions` cannot contain an
  explicit breakpoint; developer `input_text` content can. A breakpoint after a
  changed prefix does not bypass that earlier change, though an unchanged prefix
  before it may remain reusable.
- **Anthropic:** prompt caching covers all active Claude models; thinking blocks
  "cannot be cached directly with `cache_control`" but are cached alongside earlier
  assistant content; context-management tool-result clearing "invalidates cached
  prompt prefixes".
- **Ollama:** `keep_alive` controls runner residency (unloading destroys the
  in-process KV cache); context-length truncation and parallel slots change the
  reusable-prefix picture per deployment. `/v1/chat/completions` and `/api/chat`
  converge on the same internal chat request, so the rendered difference between
  routes is not the issue — **the model template is**.

A relevant counter-nuance to "transcripts are immutable to the server": an
issue-reported Ollama bug shows logically identical requests rendering to different
prompt tokens because tool-schema JSON keys appear in random order — so even
repeated identical inputs are not automatically identical rendered prefixes
([ollama#18430](https://github.com/ollama/ollama/issues/18430)). Urd should pin
byte-stable serialized tool definitions regardless of cache conclusions.

## Immutable transcripts: design policy, not cache physics

- **What caches actually require:** stability of the *rendered prefix the provider
  sees*, request to request. Append-only model-visible history + an unchanged
  rendered tool block achieves full eligibility; nothing in any investigated
  provider requires the *whole session transcript* to be frozen forever.
- **What invalidates a cache:** any prefix edit, from the first changed token —
  tools (near start), system, earlier messages, compaction. Explicit edits and
  compaction remain legitimate operations; they simply re-earn the cache from the
  change point. Urd's ARCHITECTURE.md position (append-only ordinary continuation;
  explicit edits and compaction supported) matches documented provider mechanics
  exactly.
- **Readable history vs provider request history are distinct views**, as already
  recorded: compaction changing what the model works from does not erase the user's
  history, and no provider cache depends on what Urd stores — only on what it sends.
- Pie's fixed wrapper + appended descriptors is one design that keeps the `tools`
  array frozen mid-conversation. For the examined mechanisms, that can preserve a
  matching rendered prefix when caching is available, the request meets provider
  thresholds, and routing reaches a cache with the relevant state; it is not a
  guaranteed hit, and identical prefixes alone do not guarantee cache availability
  or a hit. This is an incidental benefit, not a cache requirement, and it is not
  the only examined design: native appends can preserve earlier prefixes on OpenAI
  (`additional_tools`/`tool_search`) and Anthropic (beta), while local costs depend
  on template rendering and supported request behavior.

## Unknowns (no documented answer found on 2026-11-03)

1. OpenAI ChatGPT-plan route: effect of `store:false` on prompt caching, if any.
2. Whether a retry of a *failed* request (no response started) reuses any
   Anthropic cache written by that attempt — undocumented both directions.
3. OpenAI: whether `additional_tools`-injected tools are searchable by *hosted*
   tool_search in later turns; undocumented.
4. Ollama per-model: no catalogue of which shipped model templates render tools
   where; it must be checked per model tag, and templates can change between Ollama
   versions.
5. All cached-token savings are unmeasured for Urd's workloads; provider docs claim
   mechanism, not Urd's economics.

## Practical recommendation (research recommendation; selects no protocol)

1. **Baseline for the examined mechanisms:** keep the top-level `tools` array
   byte-stable during ordinary continuation and append-only history. This avoids a
   tools-array change in the examined routes, but cache availability, minimum-token
   thresholds, routing, and actual hits are provider- and deployment-dependent; an
   identical prefix alone does not guarantee reuse.
2. **Appending descriptors mid-conversation** (Pie-style) is
   documentation-consistent for the examined provider mechanisms: it appends
   *messages*, which their longest-prefix mechanics treat as safe. Its cost is
   prompt tokens, not a cache guarantee.
3. **Native discovery is the documented cache-preserving tool-addition path** on
   OpenAI (tool_search / additional_tools, appended at end of context by design —
   and notably *not* beta-gated per current docs, though `tool_search` needs
   gpt-5.4+) and Anthropic (mid-conversation system messages are stable;
   inline-tools remains beta, model-restricted). Owner receptivity to these fits
   the documented facts. Anthropic's beta and Ollama's absence of any equivalent
   argue for keeping the portable path as the fallback, not the exception.
4. **Do not rely on "identical request = identical cache" for repeated/retried
   calls** without pinned byte-stable serialization (see ollama#18430); pin tool
   JSON at creation time, not per request.
5. Treat compaction, explicit edits, tool-choice changes and system-prompt changes
   as **documented cache resets from the first changed token**, and let Urd make
   them deliberately (e.g. at natural boundaries) rather than avoiding them.

## Minimal future experiment (only if the cache tradeoff needs numbers)

Small, bounded, and measurable where the provider/server exposes a cache metric
(OpenAI prompt-cached-token usage fields; Anthropic cache_creation/cache_read;
verify the actual Ollama API metric before relying on any internal counter):

- **Setup:** one realistic conversation per provider (~10 turns), byte-pinned tools,
  identical requests replayed twice: A = append-only continuation each turn;
  B = same, but add one new tool to `tools` each turn.
- **Measure:** verify the actual per-server exposed cache metric first, then compare
  reported cached tokens in A vs B. Do not assume an internal source counter such as
  Ollama's `PromptEvalCachedCount` is part of the standard public API response.
- **Optional C:** same as B but with the tool change expressed as the provider's
  native append mechanism (additional_tools item / tool_addition block).
- **Local variant:** repeat only with templates and requests known to support the
  tested tool behavior; a template that does not render tools is not a useful native
  tool-discovery comparison and may indicate unsupported tool use.
- This can establish *eligibility and hit behaviour at Urd's shapes* only where a
  per-server cache metric is exposed; it is not a cost study and needs no timing rig.

## Sources

Verified verbatim on 2026-11-03 against live official docs:

- OpenAI: [Prompt caching](https://developers.openai.com/api/docs/guides/prompt-caching)·
  [Diagnostics](https://developers.openai.com/api/docs/guides/prompt-caching/diagnostics)·
  [Tool search](https://developers.openai.com/api/docs/guides/tools-tool-search)·
  [Chat Completions create](https://developers.openai.com/api/reference/resources/chat/subresources/completions/methods/create)·
  [Responses migrator](https://developers.openai.com/api/docs/guides/migrate-to-responses)·
  [SIWC inference](https://developers.openai.com/siwc/token-sharing-open-source/models-and-inference)
- Anthropic: [Prompt caching](https://platform.claude.com/docs/en/build-with-claude/prompt-caching)·
  [Mid-conversation system messages & tool changes](https://platform.claude.com/docs/en/build-with-claude/mid-conversation-system-messages)·
  [Cache diagnostics](https://platform.claude.com/docs/en/build-with-claude/cache-diagnostics)·
  [Context editing](https://platform.claude.com/docs/en/build-with-claude/context-editing)·
  [Compaction](https://platform.claude.com/docs/en/build-with-claude/compaction-on-demand)
- Ollama / llama.cpp (pinned source anchors, read-only):
  Ollama [`9a3e817`](https://github.com/ollama/ollama): request build
  [llm/llama_server.go](https://github.com/ollama/ollama/blob/9a3e8177767245acc62b735eba7d5d8a553606d0/llm/llama_server.go)·
  route/`ChatHandler` + [OpenAI middleware](https://github.com/ollama/ollama/blob/9a3e8177767245acc62b735eba7d5d8a553606d0/middleware/openai.go)·
  [renderer/template inputs](https://github.com/ollama/ollama/blob/9a3e8177767245acc62b735eba7d5d8a553606d0/server/prompt.go)·
  [llama3-instruct template (no tools)](https://github.com/ollama/ollama/blob/9a3e8177767245acc62b735eba7d5d8a553606d0/template/llama3-instruct.gotmpl)·
  [Cohere template (tools in system)](https://github.com/ollama/ollama/blob/9a3e8177767245acc62b735eba7d5d8a553606d0/template/command-r.gotmpl)·
  [FAQ](https://github.com/ollama/ollama/blob/9a3e8177767245acc62b735eba7d5d8a553606d0/docs/faq.mdx);
  llama.cpp [`19e8a86`](https://github.com/ggml-org/llama.cpp):
  [prefix matching](https://github.com/ggml-org/llama.cpp/blob/19e8a86fe87e5888f61b3aa975e8634b322bd776/tools/server/server-context.cpp)·
  [slot selection](https://github.com/ggml-org/llama.cpp/blob/19e8a86fe87e5888f61b3aa975e8634b322bd776/tools/server/README.md);
  issue-report: [ollama#18430](https://github.com/ollama/ollama/issues/18430).

Urd context consulted (not edited):
[ARCHITECTURE.md](ARCHITECTURE.md) (preserve-model-visible-prefixes direction),
[MODEL-INTERFACE-RESEARCH.md](MODEL-INTERFACE-RESEARCH.md) (adapter capabilities for
raw/native discovery), [README.md](README.md) (direction authority).