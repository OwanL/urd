# Provider integration research

## Status and limits

Read-only source review on 2026-11-02, supporting [ARCHITECTURE.md](ARCHITECTURE.md).
No language, SDK or provider implementation is selected. No accounts, credentials,
login flows or model requests were exercised. This establishes documented seams,
not working Urd integrations, account entitlement, performance or implementation
LOC. The SDK comparison is a bounded TypeScript-oriented sample, not a language
comparison or an exhaustive library survey.

Urd requires subscription sign-in, API keys and local-server connections while
owning its agent loop and avoiding Pi dependencies. Separate four responsibilities:
model transport, authentication, conversation/tool execution, and presentation.
A library can help with the first without owning the latter two. A package called
an SDK or provider is not necessarily a model-only client.

## Two model-client approaches

| Approach | Established capability | Work or uncertainty left to Urd |
| --- | --- | --- |
| Vercel AI SDK model calls | `generateText`/`streamText` support one-step calls; tool `execute` is optional. Urd can receive tool calls and own execution/continuation. Common stream/usage interfaces and provider adapters exist. | Validate each required route, request shape, usage semantics and cancellation behavior. A common interface does not supply every subscription login or make endpoints interchangeable. Its own agent/tool-loop facilities need not become Urd's runtime. |
| Raw official vendor clients | OpenAI Responses and Anthropic Messages clients expose model calls, streams, tool-call data and cancellation without requiring their higher-level agent helpers. OpenAI supports custom base URLs and bearer credentials. | Urd owns more cross-provider normalization and compatibility handling. Some credential/refresh helpers exist, but they are not proof of a complete, entitled subscription login. |

The common layer could reduce Urd-owned normalization; raw clients expose native
behavior more directly. This is a qualitative maintenance trade-off, not evidence
that either is smaller or faster overall. Local OpenAI-compatible servers can use
compatible transports, but model/tool/stream support still requires validation.
Neither approach has been qualified against Urd's complete access requirements.

## ChatGPT subscription access: a public model API path

OpenAI's current Sign in with ChatGPT documentation describes ChatGPT plan usage
for open-source and locally hosted apps. It provides a public-client registration,
OAuth/PKCE/OIDC and consent flow, with a persistent host identity and issued client
registration. The resulting scoped OAuth token can authorize Responses API calls;
the documented SDK example passes it as the bearer credential through `api_key`.
This does not require handing the agent loop to Codex app-server.

The documented route is `https://api.openai.com/v1/responses`, **not** ChatGPT's
`backend-api` route used by the inspected Pi Codex adapter. This is a reason to
research current provider contracts rather than mechanically port Pi's wiring.

Important route constraints include `store: false`, `stream: true`, and supplying
the required conversation context on each HTTP request rather than relying on
`previous_response_id`. The preview restricts request fields and tool forms:
function/custom tools must use the documented namespaces or additional-tool
input form. Generic API-key examples are therefore not sufficient compatibility
tests. Urd's local tools are distinct from unsupported provider-hosted/native tools.
A started stream is not proof of success; terminal failures and interrupted streams
must remain distinguishable from `response.completed`.

Refresh, account switching, granted scopes, available models and request shaping
still need an integration. The docs describe access and deployment conditions;
this review does not establish Urd registration or any account's entitlement.
Live documentation is not version-pinned and must be rechecked before implementation.

## Copilot subscription access: distinguish the SDK from a raw client

GitHub's documented Copilot SDK runs the Copilot CLI agent runtime over JSON-RPC.
It handles planning and tool invocation; standard usage uses a Copilot subscription,
while BYOK is a separate API-key path. It is **not** a drop-in model transport for
Urd's first-party loop merely because it is called an SDK. Choosing it as the
agent runtime would need a deliberate reconsideration of harness ownership.

Pi and the inspected independent Crush implementation show another mechanism:
GitHub OAuth/device authentication followed by a Copilot token exchange. Pi also
handles provider-specific endpoints, headers and model availability. These are
useful implementation references, not proof of a supported, stable raw API contract
or blanket provider permission. No standalone public Copilot inference contract
was established by this bounded review; that is a remaining research/qualification
gap, not a claim that such an integration is impossible or forbidden.

Do not remove Copilot or import another agent engine merely to close this gap.
First assess a focused model/auth adapter and its maintenance burden against the
existing product requirements. No provider test calls or sign-ups are implied.

## Sources

Pinned source snapshots:

- Vercel AI SDK [`ad09bc1c55e9c2ad224d17008013a32e79b8ea82`](https://github.com/vercel/ai/tree/ad09bc1c55e9c2ad224d17008013a32e79b8ea82): [tool execution and loop controls](https://github.com/vercel/ai/blob/ad09bc1c55e9c2ad224d17008013a32e79b8ea82/content/docs/03-ai-sdk-core/15-tools-and-tool-calling.mdx), [streaming implementation](https://github.com/vercel/ai/blob/ad09bc1c55e9c2ad224d17008013a32e79b8ea82/packages/ai/src/generate-text/stream-text.ts), [OpenAI-compatible adapter](https://github.com/vercel/ai/tree/ad09bc1c55e9c2ad224d17008013a32e79b8ea82/packages/openai-compatible).
- OpenAI Node SDK [`aab204589e6a512883aa4d1e63dcfafbb4490f62`](https://github.com/openai/openai-node/tree/aab204589e6a512883aa4d1e63dcfafbb4490f62): [client options and transport](https://github.com/openai/openai-node/blob/aab204589e6a512883aa4d1e63dcfafbb4490f62/src/client.ts), [Responses](https://github.com/openai/openai-node/blob/aab204589e6a512883aa4d1e63dcfafbb4490f62/src/resources/responses/responses.ts).
- Anthropic TypeScript SDK [`cb5fba419808e3844fa6407e59577ab7ba346b79`](https://github.com/anthropics/anthropic-sdk-typescript/tree/cb5fba419808e3844fa6407e59577ab7ba346b79): [Messages](https://github.com/anthropics/anthropic-sdk-typescript/blob/cb5fba419808e3844fa6407e59577ab7ba346b79/src/resources/messages/messages.ts), [OAuth refresh helper](https://github.com/anthropics/anthropic-sdk-typescript/blob/cb5fba419808e3844fa6407e59577ab7ba346b79/src/lib/credentials/user-oauth.ts).
- GitHub Copilot SDK [`68de1a7afe4bc9e7435d185825043bd9dc9c47cf`](https://github.com/github/copilot-sdk/blob/68de1a7afe4bc9e7435d185825043bd9dc9c47cf/README.md): architecture, runtime ownership, authentication and BYOK distinctions.
- Crush [`d391cee20d1942794aec5ea6faff07170444211f`](https://github.com/charmbracelet/crush/blob/d391cee20d1942794aec5ea6faff07170444211f/internal/oauth/copilot/oauth.go): independent Copilot OAuth/token-exchange implementation.
- Pie `cc82abb0f317f31341d3a6f12aa1545f6be1fbea`: `harness/model-providers/management/pi-auth.ts`; `harness/pi/packages/ai/src/utils/oauth/{openai-codex,github-copilot}.ts`; `harness/pi/packages/ai/src/api/openai-codex-responses.ts`. Local provider source, not a live account/activation check.

Live official documentation inspected on the review date:

- OpenAI [Sign in with ChatGPT overview](https://developers.openai.com/siwc/token-sharing-open-source.md), [sign-in](https://developers.openai.com/siwc/token-sharing-open-source/sign-in.md), [models and inference](https://developers.openai.com/siwc/token-sharing-open-source/models-and-inference.md), [preview limitations](https://developers.openai.com/siwc/token-sharing-open-source/preview-limitations.md), [token reference](https://developers.openai.com/siwc/token-sharing-open-source/token-reference.md).
- Ollama [OpenAI compatibility](https://docs.ollama.com/api/openai-compatibility).
