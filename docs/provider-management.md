# Headless provider management

The local server manages saved **ChatGPT subscription**, **OpenAI API-key** and
**custom OpenAI-compatible** connections. ChatGPT uses Responses; custom endpoints
choose Responses or Chat Completions, with an API key or no authentication.
A shared request-only model client now supports conversation consumers as well as future
pruning, compaction and title operations. Provider selection/HTTP/UI composition is a separate
integration checkpoint; this does not add those auxiliary operations, discovery tools,
other subscription providers or native Ollama/Anthropic adapters.

## Storage and ownership

`UrdStore` replaces the former `SessionStore` name and owns the same SQLite database.
Schema 6 adds provider connections and a lazily created host identity; the transactional
schema-5 migration leaves session evidence intact. There is no second database or lease.
`ProviderManager` borrows the store, owns its HTTP transport, and drains requests,
sign-ins and refresh/revocation work before disposal. The host joins this drain with
its existing HTTP/session shutdown barrier.

Credentials are **plaintext in SQLite**, including its journal/WAL files and backups.
Keep the data directory out of Git and do not share database copies. HTTP reads expose
configuration and credential status, never keys, tokens, refresh credentials or PKCE
verifiers. Browser sign-in URLs necessarily contain ephemeral public OAuth parameters.
Only send keys to endpoints you trust. Custom HTTP endpoints are permitted; they do
not provide HTTPS transport protection.

Configuration generation and credential version start at zero. Binding changes and
credential replacement increment both; token refresh increments only credential
version. Renames preserve credentials and catalogs. Custom endpoint/API/authentication
changes clear credentials and catalogs. Same-grant token rotation preserves a catalog;
a changed grant invalidates it and cannot be undone by a late discovery response.

## Local HTTP contract

Start the [local server](server-development.md#run) and use its actual readiness origin.
All `/api` requests require `X-Urd-Client: browser`. The existing loopback-peer,
exact-Host/Origin rules apply; responses are `Cache-Control: no-store`.
POST bodies require JSON; commands shown as `{}` require an empty object.
JSON member/enum names are camelCase. There are no implicit network calls on GET.

Paths below start with `/api/providers`:

| Method and path | Body → successful response |
| --- | --- |
| GET `/` | Saved `ProviderConnection[]` |
| POST `/chatgpt` | `{name}` → 201 connection |
| POST `/openai` | `{name}` → 201 connection |
| POST `/custom` | `{name, api, endpoint, authentication}` → 201 connection |
| GET `/{id}` | Connection |
| POST `/{id}/rename` | `{name}` → connection |
| POST `/{id}/custom-settings` | Custom settings → connection |
| POST `/{id}/remove` | `{}` → 204; deletes local configuration and credentials |
| POST `/{id}/api-key` | `{connection, apiKey}` → credential status |
| GET `/{id}/credentials` | Credential status, not credential values |
| POST `/{id}/sign-in` | `{}` → attempt and browser authorization URL |
| GET `/sign-ins/{signInId}` | Attempt status |
| POST `/sign-ins/{signInId}/cancel` | `{}` → final attempt status |
| POST `/{id}/disconnect` | `{}` → `{connectionId, localCredentialsCleared, remoteRevocationConfirmed}` |
| GET `/{id}/models` | Last successful catalog, including `refreshedAt`, or an empty unsampled catalog |
| POST `/{id}/models/refresh` | `{}` → fetched and saved catalog |
| POST `/{id}/model-tests` | `{modelId}` → response preview and available usage |

A connection is `{id, name, route, api, endpoint, authentication}`. Custom `api` is
`openAIResponses` or `openAIChatCompletions`; `authentication` is `apiKey` or `none`.
Endpoints are HTTP(S) base URLs without credentials, query or fragment. Prefix paths
are retained and the trailing slash is normalized. API-key input includes the complete
connection view read by the caller: a stale binding is rejected, while a rename is safe.

## ChatGPT-first flow

1. Add `/chatgpt`, then POST its `/sign-in`. Open the returned `authorizationUrl`
   yourself; the backend does not launch a browser. Repeated begin joins the same
   pending attempt. Attempts expire after ten minutes and do not survive restart.
2. The provider returns to `http://127.0.0.1:<actual-port>/auth/callback`. This GET does
   not require the API marker, but retains local peer/Host/Origin checks and no-store.
   State, PKCE, issued client ID, signed issuer/audience/expiry/nonce and returning
   account binding are checked before credentials are saved. Duplicate/stale callbacks
   cannot replace a newer attempt. The page contains only a static completion message.
3. Poll the attempt and read `/credentials`. Authentication, required plan scopes,
   discovery and completed inference are separate observations. Missing
   `resource.invoke` or `chatgpt.tokens.use.direct` prevents plan requests; it never
   selects paid API credentials as a fallback.
4. Explicitly refresh `/models`. ChatGPT uses the plan `models[]` envelope, retaining
   visible entries in provider order. Custom discovery may be unsupported: supply an
   explicit model ID to `/model-tests` without first fetching a catalog.
5. With separate approval, POST `/model-tests` once. The fixed short greeting prompt
   requests streaming output with `store:false`, no tools, conversation, generation
   knobs or automatic retries. Only a positive native terminal followed by stream
   cleanup counts as success. The preview is limited to 4096 characters; absent usage
   stays unknown. This is **not a spending cap** and does not create a saved chat.

Configuration/authentication commands and catalog refresh remain manager-owned if the
HTTP caller disconnects. Diagnostic cancellation follows its caller; network diagnostics
have a two-minute deadline. OAuth HTTP calls have thirty-second limits. Cancellation
cannot undo an already accepted provider request or guarantee no charge.

Refresh is shared per connection. Definite transient failures preserve the known grant
for a later explicit operation; rejected refresh credentials require sign-in. A lost token
exchange acknowledgement is not retried: the manager marks sign-in required, retaining
known credentials for disconnect. If that recovery write itself fails, the running
manager remains fenced; durable recovery cannot be promised when SQLite cannot write.
No rotated token is used before successful persistence.

Disconnect first saves a durable admission fence, cancels/joins provider work, waits for
any rotating refresh, then attempts revocation using the latest known refresh token.
It clears local tokens/catalog while retaining registration and host identity. A null
`remoteRevocationConfirmed` means no revocation was applicable; false means not confirmed. An
interrupted disconnect stays fenced after restart and can be explicitly repeated.
Remove only deletes local state; disconnect first when remote revocation is wanted.

## Shared model requests

`ProviderManager.CreateChatClient(expectedBinding)` returns a borrowed `IChatClient`.
Construction is offline; requests supply `ChatOptions.ModelId` and optional
`Reasoning.Effort`. Conversations and auxiliary operations share authentication, refresh,
request validation and transport without inheriting each other's session state. Disposing
a client view does not dispose the manager or another consumer's transport.

Both Responses and compatible Chat Completions stream through the official SDK/MEAI
adapter. Ordinary requests have no Urd wall-clock cutoff: callers can impose a deadline,
and Stop, disconnect or shutdown still cancel and drain owned work. Diagnostics retain
their separate two-minute limit. There are no automatic retries, fallback providers,
provider-hosted conversations or tool execution inside the client. `GetResponseAsync`
consumes the same guarded streaming path, not a second unvalidated implementation.

Text streams incrementally. Executable calls, final completion and native context are
withheld until the positive native terminal, complete SDK iteration/disposal, cancellation
callback drain and final manager grant check all succeed. A changed binding cannot redirect
a stale client to a new endpoint; a rename is harmless. Already accepted completion does
not become invalid merely because the caller later replaces credentials.

Supported requests are text/function/reasoning exchanges, with `store:false` and full
history. Responses requests include encrypted reasoning for stateless continuation.
ChatGPT function declarations use the permitted `urd` namespace; this is offline wire
qualification, not live tool/discovery qualification. Unsupported content/options are
rejected rather than silently omitted, including named speakers, hosted tools, raw option
factories and explicit strict-function schema settings on the plan namespace path.
Protocol support for a reasoning effort does not establish support by a particular model;
current ID/name catalogs leave that compatibility unknown. No model-name heuristic fills it in.

`LlmProviderContext` captures a versioned native response scoped to connection ID, binding
generation, API and exact **requested** model ID, not the provider's resolved alias.
Same-scope replay retains ordered native items, function IDs and private reasoning. An
explicit changed scope retains portable visible text/calls while omitting private reasoning;
nonportable visible content fails explicitly. Native payloads have a 4 MiB UTF-8 limit and
are rejected rather than truncated. The codec retains appended-definition representations
without implementing a discovery policy. Schema 7 stores this envelope in
`execution_provider_context`, atomically with successful request completion and tool-call
authorization. Context reconstruction restores it instead of flattening native history;
legacy responses remain supported. Tool results remain paired and historical effects are
never replayed to repair context. Private reasoning is not appended to public transcript text.

## Qualification

2026-11-08: locked restore and .NET 10.0.403 Release build passed with zero warnings/errors;
the complete backend suite passed **453/453 tests**, with independent acceptance review.

Offline tests exercise the actual Duende, Microsoft JWT and official OpenAI SDK paths
against socket-free synthetic transports, plus real local Kestrel and SQLite workflows.
They cover signed sign-in, refresh/disconnect races, stale results, storage migration,
reopen, terminal/error streaming and shutdown ownership.

A separately authorized live check on 2026-11-08 completed fresh ChatGPT OAuth through
Urd's local callback: identity validation and credential persistence succeeded, with both
required plan scopes present. On the owner's request, disconnect then cleared local
credentials and the provider acknowledged revocation. The isolated test process was
stopped and its entire temporary data directory deleted. No existing credentials were
reused, and no catalog or model request was sent. Live refresh, restart persistence,
model entitlement, responses and billing behavior remain unqualified.
