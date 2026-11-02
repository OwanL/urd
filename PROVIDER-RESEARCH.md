# Provider integration research

## Status and limits

Source review and bounded offline checks on 2026-11-02–03, supporting
[ARCHITECTURE.md](ARCHITECTURE.md).
No language, SDK or provider implementation is selected. No accounts, credentials,
login flows or model requests were exercised. This establishes documented seams,
not working Urd integrations, account entitlement, performance or implementation
LOC. The SDK comparison is a bounded TypeScript-oriented sample, not a language
comparison or an exhaustive library survey.

Urd requires subscription sign-in, API keys and local-server connections, a
lightweight first-party harness and no Pi dependency. **Ownership of the model/tool
loop is open.** This study initially assumed Urd must own that loop; the assumption
was not owner-approved and has been withdrawn. Its model-client findings remain
useful, but they do not select a loop implementation. Subsequent owner alignment
excluded complete agent runtimes as Urd's foundation: build a lightweight harness
for Urd's needs, based on Pie's product direction. Focused libraries remain
eligible; the full-runtime comparisons below are historical research.

Separate model transport, authentication, conversation/tool execution, and
presentation. A library can help with transport without owning execution or UI;
reusing a loop is another candidate, not automatically forbidden. A package
called an SDK or provider is not necessarily a model-only client.

## Two model-client approaches

| Approach | Established capability | Work or uncertainty left to Urd |
| --- | --- | --- |
| Vercel AI SDK model calls | `generateText`/`streamText` support one-step calls; tool `execute` is optional. Urd can receive tool calls and own execution/continuation. Common stream/usage interfaces and provider adapters exist. | Validate each required route, request shape, usage semantics and cancellation behavior. A common interface does not supply every subscription login or make endpoints interchangeable. Its loop-running facilities are compared below; complete Urd behaviour remains unqualified. |
| Raw official vendor clients | OpenAI Responses and Anthropic Messages clients expose model calls, streams, tool-call data and cancellation without requiring their higher-level agent helpers. OpenAI supports custom base URLs and bearer credentials. | Urd owns more cross-provider normalization and compatibility handling. Some credential/refresh helpers exist, but they are not proof of a complete, entitled subscription login. |

The common layer could reduce Urd-owned normalization; raw clients expose native
behavior more directly. This is a qualitative maintenance trade-off, not evidence
that either is smaller or faster overall. Local OpenAI-compatible servers can use
compatible transports, but model/tool/stream support still requires validation.
Neither approach has been qualified against Urd's complete access requirements.

## Authentication libraries are not subscription entitlements

The owner wants popular subscription support and prefers library reuse for
authentication/token management. OAuth is an authorization protocol; Auth0 is a
hosted identity service, not the name of that protocol.

For example, `openid-client` documents authorization-code/PKCE, device authorization
and refresh-token flows. It is a JavaScript candidate, not a language or library
selection. Auth0 Token Vault also manages third-party access/refresh tokens and
token exchange, but introduces a hosted identity/token service. The owner chose
direct provider sign-in with locally managed credentials, not a required hosted
identity/token broker. This does not select an OAuth library or credential store.

Neither a generic OAuth client nor token storage establishes that a consumer AI
subscription permits Urd to call a model API. Provider registration, scopes,
entitlement, endpoints and request shaping still need qualification; a focused
provider adapter may supply that work without a complete agent runtime.

## Loop ownership: compare the same model-client layer

An app-owned loop can reuse the **same** AI SDK adapters, streaming and tool-call
handling as an SDK-managed loop. The comparison is repeated single-step
`streamText` calls versus an SDK-managed multi-step call, not handwritten provider
protocols versus a library. `ToolLoopAgent` is a wrapper over these primitives,
not a separate model engine. No maintained-LOC advantage has been measured.

An independent pinned-source review established useful control boundaries:

- Observational callbacks such as `onStepEnd` are awaited, but their exceptions
  are swallowed. A rejected persistence write there alone does not stop the loop.
- `prepareStep` is a directly awaited control hook. Rejecting there can prevent
  the next model call without patching the SDK. This does not undo current-step
  tools; initially approved tools can also replay before the first `prepareStep`.
- Both loop shapes can execute a whole tool batch inside one SDK call. Owning
  the outer loop alone does not establish per-tool admission or cancellation.
  The concurrency mapper's unlimited/full-batch path lacks its limited-worker
  abort check; in-flight tools must cooperate with their abort signal.
- A human-question tool can await an answer or omit execution and supply a result
  in a later invocation. An ignored abort signal does not terminate a pending
  tool promise. Later invocation requires coherent call IDs/messages; it is not
  SDK-provided durable continuation, and approval is a different protocol.

Three offline checks of pinned helper bodies confirmed observer error swallowing,
queue/fast-path abort differences and cooperative tool cancellation. These are
not complete `streamText` or Urd integration tests. Reproducible artifacts are in
`%TEMP%\urd-loop-review-ec7fbacd\`. Source evidence supports testing the managed
loop, not declaring either loop shape qualified or smaller.

### Narrow published-release integration check

A separate probe used published `ai@7.0.159`, whose registry provenance identifies
source commit [`028a025398782f4938303f48fbeee45c79af544b`](https://github.com/vercel/ai/tree/028a025398782f4938303f48fbeee45c79af544b).
This is **not** the earlier source-review pin. Relevant installed source files
were matched to this release. Each case used the real `streamText`, the SDK's
mock model, one harmless counter tool and a failing save operation:

| Condition | Model calls observed | Result |
| --- | ---: | --- |
| SDK-managed loop; save rejects in `onStepEnd` | 2 | Error swallowed; normal completion. |
| SDK-managed loop; save rejects in next `prepareStep` | 1 | Next model call prevented; error emitted in the stream. |
| Single-step caller; save rejects before calling SDK again | 1 | Next invocation prevented by the caller. |

All assertions passed, with independent review and repeat runs. Crucially, the
`prepareStep` failure did **not** throw during stream consumption or reject the
finish-reason promise; consumers must inspect error events, not treat completion
without a thrown exception as success.
The first tool had already executed in every case. This tests a next-model gate,
not rollback, real provider transport, restart recovery or overall Urd fitness.
Artifacts and the dependency lockfile are in `%TEMP%\urd-release-probe-f6a94b69\`.

## Managed runtimes: researched, excluded as Urd's foundation

The inspected Copilot SDK/CLI and Codex app-server expose more than model calls:
they own execution, compaction and persisted conversation state. Public contracts
expose readable history apart from compacted context, suffix removal, interruption,
custom-tool seams and deletion. Some APIs are experimental; required provider
routes, helper inspection, cleanup and recovery still need qualification.

The research considered whether reusing their transcript stores could avoid
history implementation in Urd. The owner has since rejected building Urd on a
complete agent runtime. Potential first-party LOC savings do not override the
direction to build Urd's own lightweight harness. This is an architectural scope
decision, not a finding that these runtimes cannot work or that focused libraries
must not run a model/tool loop.

History pagination needs separate scrutiny. At the newer Codex pin below,
projected paginated history queries SQLite rather than replaying the full rollout;
legacy mode still replays it, and cold model resume is a separate read path.
Returned rows are bounded, not necessarily all physical IO or lineage checks.
Copilot exposes backward pages, including inactive journals, but its public SDK
source does not establish the closed runtime's underlying read/parse work.
Neither finding establishes click-to-content latency or Urd's exploratory scale.

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
an app-owned loop merely because it is called an SDK. Its complete agent runtime
is excluded as Urd's foundation; do not treat it as a focused model client or
silently introduce that runtime to obtain subscription access.

Pi and the inspected independent Crush implementation show another mechanism:
GitHub OAuth/device authentication followed by a Copilot token exchange. Pi also
handles provider-specific endpoints, headers and model availability. These are
useful implementation references, not proof of a supported, stable raw API contract
or blanket provider permission. No standalone public Copilot inference contract
was established by this bounded review; that is a remaining research/qualification
gap, not a claim that such an integration is impossible or forbidden.

Qualify focused model/auth adapter options without dropping the agreed access
categories. Exact Copilot coverage remains open. If a required access route
conflicts with the harness boundary, bring that trade-off to the owner rather
than silently changing either requirement. No adapter is selected, and no
provider test calls or sign-ups are implied.

## Sources

Pinned source snapshots:

- Vercel AI SDK [`ad09bc1c55e9c2ad224d17008013a32e79b8ea82`](https://github.com/vercel/ai/tree/ad09bc1c55e9c2ad224d17008013a32e79b8ea82): [tool execution and loop controls](https://github.com/vercel/ai/blob/ad09bc1c55e9c2ad224d17008013a32e79b8ea82/content/docs/03-ai-sdk-core/15-tools-and-tool-calling.mdx), [streaming implementation](https://github.com/vercel/ai/blob/ad09bc1c55e9c2ad224d17008013a32e79b8ea82/packages/ai/src/generate-text/stream-text.ts), [OpenAI-compatible adapter](https://github.com/vercel/ai/tree/ad09bc1c55e9c2ad224d17008013a32e79b8ea82/packages/openai-compatible).
- OpenAI Node SDK [`aab204589e6a512883aa4d1e63dcfafbb4490f62`](https://github.com/openai/openai-node/tree/aab204589e6a512883aa4d1e63dcfafbb4490f62): [client options and transport](https://github.com/openai/openai-node/blob/aab204589e6a512883aa4d1e63dcfafbb4490f62/src/client.ts), [Responses](https://github.com/openai/openai-node/blob/aab204589e6a512883aa4d1e63dcfafbb4490f62/src/resources/responses/responses.ts).
- Anthropic TypeScript SDK [`cb5fba419808e3844fa6407e59577ab7ba346b79`](https://github.com/anthropics/anthropic-sdk-typescript/tree/cb5fba419808e3844fa6407e59577ab7ba346b79): [Messages](https://github.com/anthropics/anthropic-sdk-typescript/blob/cb5fba419808e3844fa6407e59577ab7ba346b79/src/resources/messages/messages.ts), [OAuth refresh helper](https://github.com/anthropics/anthropic-sdk-typescript/blob/cb5fba419808e3844fa6407e59577ab7ba346b79/src/lib/credentials/user-oauth.ts).
- AI SDK control paths (same `ad09bc1c` pin): [observer notification](https://github.com/vercel/ai/blob/ad09bc1c55e9c2ad224d17008013a32e79b8ea82/packages/ai/src/util/notify.ts), [tool concurrency](https://github.com/vercel/ai/blob/ad09bc1c55e9c2ad224d17008013a32e79b8ea82/packages/ai/src/generate-text/tool-call-concurrency.ts), and [tool execution](https://github.com/vercel/ai/blob/ad09bc1c55e9c2ad224d17008013a32e79b8ea82/packages/provider-utils/src/types/execute-tool.ts). Step/control ordering is in the streaming implementation above.
- GitHub Copilot SDK [`68de1a7afe4bc9e7435d185825043bd9dc9c47cf`](https://github.com/github/copilot-sdk/blob/68de1a7afe4bc9e7435d185825043bd9dc9c47cf/README.md): architecture/auth; [persistence](https://github.com/github/copilot-sdk/blob/68de1a7afe4bc9e7435d185825043bd9dc9c47cf/docs/features/session-persistence.md), [steering/queues](https://github.com/github/copilot-sdk/blob/68de1a7afe4bc9e7435d185825043bd9dc9c47cf/docs/features/steering-and-queueing.md), and [RPC contracts](https://github.com/github/copilot-sdk/blob/68de1a7afe4bc9e7435d185825043bd9dc9c47cf/nodejs/src/generated/rpc.ts).
- Codex [`3026a8e54e0f48c4d8074cd700f35b0697abc329`](https://github.com/openai/codex/tree/3026a8e54e0f48c4d8074cd700f35b0697abc329): [app-server contracts](https://github.com/openai/codex/blob/3026a8e54e0f48c4d8074cd700f35b0697abc329/codex-rs/app-server/README.md), [page handler](https://github.com/openai/codex/blob/3026a8e54e0f48c4d8074cd700f35b0697abc329/codex-rs/app-server/src/request_processors/thread_processor.rs), [paged queries](https://github.com/openai/codex/blob/3026a8e54e0f48c4d8074cd700f35b0697abc329/codex-rs/thread-store/src/local/thread_history/segment_paging.rs), and [model-context reconstruction](https://github.com/openai/codex/blob/3026a8e54e0f48c4d8074cd700f35b0697abc329/codex-rs/thread-store/src/local/model_context.rs). This is a separate, later snapshot than the initial storage comparison.
- Crush [`d391cee20d1942794aec5ea6faff07170444211f`](https://github.com/charmbracelet/crush/blob/d391cee20d1942794aec5ea6faff07170444211f/internal/oauth/copilot/oauth.go): independent Copilot OAuth/token-exchange implementation.
- Pie `cc82abb0f317f31341d3a6f12aa1545f6be1fbea`: `harness/model-providers/management/pi-auth.ts`; `harness/pi/packages/ai/src/utils/oauth/{openai-codex,github-copilot}.ts`; `harness/pi/packages/ai/src/api/openai-codex-responses.ts`. Local provider source, not a live account/activation check.

Live official documentation inspected on 2026-11-02:

- OpenAI [Sign in with ChatGPT overview](https://developers.openai.com/siwc/token-sharing-open-source.md), [sign-in](https://developers.openai.com/siwc/token-sharing-open-source/sign-in.md), [models and inference](https://developers.openai.com/siwc/token-sharing-open-source/models-and-inference.md), [preview limitations](https://developers.openai.com/siwc/token-sharing-open-source/preview-limitations.md), [token reference](https://developers.openai.com/siwc/token-sharing-open-source/token-reference.md).
- Ollama [OpenAI compatibility](https://docs.ollama.com/api/openai-compatibility).

Authentication documentation inspected on 2026-11-03:

- [openid-client](https://github.com/panva/openid-client): supported OAuth/OIDC flows and token operations.
- [Auth0 Token Vault](https://auth0.com/docs/secure/tokens/token-vault): hosted third-party token storage and exchange; not proof of AI subscription entitlement.
