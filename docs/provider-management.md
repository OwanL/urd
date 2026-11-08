# Headless provider management

The local server manages saved **ChatGPT subscription**, **OpenAI API-key** and
**custom OpenAI-compatible** connections. ChatGPT uses Responses; custom endpoints
choose Responses or Chat Completions, with an API key or no authentication.
This does not connect providers to chat execution or add a provider UI, agent tools,
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

## Qualification

2026-11-08: locked restore and .NET 10.0.403 Release build passed with zero warnings/errors;
the complete backend suite passed **453/453 tests**, with independent acceptance review.

Offline tests exercise the actual Duende, Microsoft JWT and official OpenAI SDK paths
against socket-free synthetic transports, plus real local Kestrel and SQLite workflows.
They cover signed sign-in, refresh/disconnect races, stale results, storage migration,
reopen, terminal/error streaming and shutdown ownership. No personal credentials,
real sign-in, provider entitlement, model availability, inference or billing outcome
has been qualified. Those require separate authorization and fresh live evidence.
