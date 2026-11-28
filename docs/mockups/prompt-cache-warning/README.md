# Prompt cache warning

[Interactive mockup](index.html) · [Screenshot](preview.png). The numbers are
synthetic. This is a proposal, not a change to the composer or provider requests.

## When it should warn

- Put a small amber triangle beside the context count. Open the cache detail from
  the triangle by click or keyboard; do not change the context button’s Compact
  action or use the context-capacity warning colour on the token count.
- Warn below **60%** of total measured input tokens on comparable continuation
  requests. Skip the first cold request, not the initial instructions on subsequent
  requests: those instructions are part of the prefix we expect to reuse. Leave the
  existing Last turn and Session statistics unchanged; show the post-warm-up scope
  in the warning detail.
- Require at least two settled, provider-measured continuation requests on the
  same cache-reporting route and model with a stable, eligible prefix large enough
  to expect at least 60% reuse. Start fresh after a provider/model switch, compaction
  or another prefix reset. Too little data, partial or absent cache reporting, and
  mostly new input do not trigger a warning. Explicit zero counts can qualify.

The current summary excludes missing or estimated counts but aggregates all settled
conversation requests, including the first one. It does not encode prefix eligibility
or cache boundaries, so applying 60% to the existing Session row would be misleading.
See [prompt-cache semantics](../../prompt-cache-semantics.md) and
[the current query](../../../source/backend/harness/storage/usage/UsageStorage.cs#L117-L153).

## Provider investigation

This is a source and fixture check, not a measurement of any signed-in account.
Urd [only sends its durable session ID as `session-id`](../../../source/backend/harness/providers/openai/OpenAIInferenceClient.cs#L29-L34)
on the built-in ChatGPT route. A cache count that is absent does not become zero:
the Chat Completions and Responses parsers preserve field presence, and the summary
excludes missing or estimated observations.

**GitHub Copilot.** Urd [selects a per-model endpoint](../../../source/backend/harness/providers/copilot/GitHubCopilotModels.cs#L75-L85)
from the Copilot catalog: Anthropic `/v1/messages` when advertised for an
Anthropic model, otherwise `/responses` when advertised, or `/chat/completions`
when available. On Messages, Urd already parses `cache_read_input_tokens` but
[does not send `cache_control`](../../../source/backend/harness/providers/anthropic/AnthropicRequest.cs#L26-L45).
[Anthropic's Messages API](https://platform.claude.com/docs/en/build-with-claude/prompt-caching)
requires that opt-in, and the
[official VS Code Copilot client](https://github.com/microsoft/vscode-copilot-chat/blob/5863f5a7088958050792b5dccbe8b46c6e13eccc/src/platform/endpoint/node/messagesApi.ts#L192-L196)
places ephemeral cache breakpoints on this route. That is the strongest lead;
it is not live confirmation for this account/model.

For `/responses`, the official client has an experimental, default-off
[`prompt_cache_key`](https://github.com/microsoft/vscode-copilot-chat/blob/5863f5a7088958050792b5dccbe8b46c6e13eccc/src/platform/endpoint/node/responsesApi.ts#L90-L93)
derived from conversation ID and model family. Its Chat Completions serializer
can emit a distinct
[`copilot_cache_control`](https://github.com/microsoft/vscode-copilot-chat/blob/5863f5a7088958050792b5dccbe8b46c6e13eccc/src/platform/networking/common/openai.ts#L187-L189)
when a message carries a cache breakpoint. Neither is the ChatGPT `session-id`
header. Urd sends none of those Copilot cache hints today; test by route rather
than adding a generic session header.

**Ollama.** The preset creates a custom Chat Completions connection at
`127.0.0.1:11434/v1`, not a native `/api/chat` integration; it can also be pointed
elsewhere. Urd requests streaming usage (`stream_options.include_usage`) and
reads `usage.prompt_tokens_details.cached_tokens`. In
[Ollama 0.40.13](https://github.com/ollama/ollama/blob/v0.40.13/openai/openai.go#L263-L274),
the adapter only emits that nested count when its runner/backend reported the
native count. A missing count means **Not reported**, not `0%`. Ollama's optional
`keep_alive` controls model residency, not per-chat affinity, and the preset does
not override it. A loopback endpoint can still proxy a cloud model. Check local
models, proxied cloud models, and direct cloud endpoints separately.

Ollama's optional **Responses** endpoint is an exception: the
[0.40.13 Responses converter](https://github.com/ollama/ollama/blob/v0.40.13/openai/responses.go#L2100-L2107)
can serialize an unavailable native cached count as `cached_tokens: 0`.
Urd faithfully displays that wire value, but it is not reliable evidence of a
miss. Do not enable the warning on that route until reporting is qualified.

**Next check.** Use a synthetic prefix above the selected model's cache minimum,
followed by two short continuations on one model/endpoint with unchanged tools
and instructions. Check the actual request prefix, input counts, reported cache
reads and writes, and distinguish each absent field from a reported zero. For
Copilot Messages, compare with and without a supported cache breakpoint. Do not
infer a hit from lower latency alone. Live cloud requests were not run.

**Local Ollama check (2026-11-28).** On 0.40.19, a resident `qwen3.5:9b` with
synthetic append-only input gave the same results on native `/api/chat` and
streaming compatible `/v1/chat/completions` with usage enabled. Each route used
an independent stable prefix. Input/cached tokens were **3,073/0**, **3,096/3,069**,
and **3,119/3,092**. The first zero was reported; it was not a missing field.
This verifies local reuse on that model and route, not cloud behavior or the
Responses API. The [0.40.19 Responses converter](https://github.com/ollama/ollama/blob/v0.40.19/openai/responses.go#L1493)
still substitutes zero when the native cache count is missing.
