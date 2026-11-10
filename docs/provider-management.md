# Provider management

The local server manages saved **ChatGPT subscription**, **OpenAI API-key** and
**custom OpenAI-compatible** connections. ChatGPT uses Responses; custom endpoints
choose Responses or Chat Completions, with an API key or no authentication. The shared
request client powers chat execution; the provider settings UI is integrated into the browser
workspace. Future pruning, compaction and title operations remain separate consumers. This
adds no discovery tools, other subscription providers or native Ollama/Anthropic adapters.

## Storage and ownership

`UrdStore` replaces the former `SessionStore` name and owns the shared SQLite database.
The current schema is version 10: provider connections and host identity began in schema 6;
subsequent versions added native provider context, frozen selections, transcript metadata and
request usage, then attempt provenance and not-sent tracking. There is no second database or lease.
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

## Browser settings

The workspace gear opens a compact Settings panel following Pie's header/search/category
composition, initially with one **Providers** page. `source/ui/settings/Settings.tsx` owns
the shell; `source/ui/providers/settings/` owns connection forms and operation state.
Shared UI colour tokens and popover controls keep it aligned with the workspace.
The initial view has a compact **Add provider** header action and a plain list of set-up
providers, without routine connected-status labels. Setup means saved credentials or a
no-auth configuration, not current validity, model discovery or plan entitlement. Expired
credentials remain in the list with an attention label. Disconnect or binding changes that
clear required credentials return the connection to unfinished setup; no records are deleted
by this presentation rule. Unknown credential reads are reported rather than treated as
unfinished or silently omitted without explanation.

Add opens a chooser that distinguishes account sign-in, API key and custom/local servers.
Its **Finish setup** section resumes saved configurations that still need credentials;
**Check setup** keeps failed/unknown reads reachable. Successful authentication offers **Done**
to return to the main list. Selecting a set-up provider opens its connection view; Back
returns to the list and search can
return directly to matching providers without stealing typing focus. Connected providers do
not show setup forms. Configuration, model refresh and removal live under **Advanced**.
A first-time **Load models** action stays visible until a usable connection has fetched its
catalog, so ordinary setup does not require exploring advanced settings.
New configurations are named from their hostname without a name field; presets display their
recognizable provider name and custom connections display their hostname. Existing stored
names are not migrated. Successful creation opens authentication; rejected custom creation
retains its draft.

The page supports ChatGPT, OpenAI API-key and custom compatible connections: create,
custom binding changes, replace-only keys, sign-in/status/cancel, disconnect,
local removal and explicit model refresh. Binding changes, disconnect and removal require
inline confirmation because they may cancel active work. Removal does not promise remote
revocation. A connection status is not proof of model entitlement.

The owner remains mounted when the panel closes. Metadata refreshes automatically every
10 seconds while visible, every 2 seconds during pending sign-in, and on returning to the
panel. Read failures recover at the slower interval; there is no manual status button.
Reads stop while closed/hidden; accepted commands are not cancelled, automatically retried
or forgotten on panel reopen. Lost acknowledgements automatically reread saved state but
remain uncertain until the user explicitly permits another command. This is not a durable command receipt
across a full browser restart. The backend owns authentication regardless of panel lifetime.
Sign-in opens through an explicit external link; completion is observed through status
reads, not inferred from a popup closing. Key fields clear on dispatch, selection change
and close, and are never saved in browser storage. The UI discloses plaintext local storage.
Provider changes trigger offline catalog rereads without silently replacing chat selection.

Isolated captured-server Chromium checks covered create/edit/key/disconnect/remove,
catalog refresh, destructive confirmation, accepted Create with a lost acknowledgement,
close/reopen, duplicate guards, search, focus restoration, retained chat drafts, mobile and
short-height scrolling. OAuth UI transitions were intercepted with synthetic statuses;
this UI check made **no real sign-in or inference requests**. The earlier live backend
qualification below is separate. UI static checks, unit tests and the Vite build passed;
independent code and visual reviews approved. The later layout reorganization was checked
on desktop/mobile with stateful synthetic API responses, including rejected creation,
create during initial loading, close/reopen during creation, key clearing and removal.
Hostname setup and automatic status updates were also checked, including draft retention,
no polling after close, and no automatic mutations or model discovery.
The progressive-disclosure flow additionally covered back/search focus, hidden advanced
controls, API-key replacement, sign-in handoff/cancel/completion and narrow/short viewports.
Set-up-only membership was checked with no-auth locals, expired credentials, unknown reads,
resuming unfinished authentication, completing setup, disconnecting and pending creation
across close/reopen. These are membership rules, not an online health check.
These layout checks do not add backend or live-authentication qualification.
The temporary fixture process/database was
removed; shared workspace integration and palette/tab changes are coordinated separately.

## Usage and provider removal

A connection is mutable configuration, not the identity store for historical usage.
Submitted selections snapshot the connection ID, route, API, endpoint, requested model and
reasoning option independently of `provider_connections`. Removing or editing a connection
does not rewrite that snapshot or delete session receipts. Hostnames and display labels are
not unique accounting keys; recreating the same host must not merge distinct connections.

For chat requests, when a provider response reports usage, Core accumulates and persists
the usage details with that request's completion in `execution_requests.usage_json`. These
values are not exposed as a summary or price. Missing values remain unknown, never guessed from a live connection
or silently counted as zero. The submitted selection preserves its secret-free provider/API/
endpoint/model binding, but account attribution and provider-resolved model IDs are not
established by the current usage record. Credentials, OAuth registration, catalogs and native
response envelopes are not required for usage retention.

A compact post-closure summary still needs its own design and attribution contract; follow the
[compact post-closure summary policy](ARCHITECTURE.md#keep-small-usage-summaries-after-closure).
This does not require retaining conversations or adding a deleted-provider registry now.

## Qualification

2026-11-08: the provider-management checkpoint passed **453/453 tests**. A later shared-request
and native-session checkpoint, including live-discovered compatibility fixes, passed locked
restore and a zero-warning .NET 10.0.403 Release build in an isolated source archive.
Independent acceptance reviews approved the changes.

Offline tests exercise the actual Duende, Microsoft JWT and official OpenAI SDK paths
against socket-free synthetic transports, plus real local Kestrel and SQLite workflows.
They cover signed sign-in, refresh/disconnect races, stale results, current-schema storage,
reopen, terminal/error streaming and shutdown ownership.

Separately authorized live checks completed fresh ChatGPT OAuth and then qualified the
owner-selected `gpt-6-luna` Responses path: catalog retrieval, streamed text, a harmless
function/result round trip, SQLite continuation without repeating the effect, and public
Stop through cancelled settlement. The live tool check exposed a required namespace
description missing from the request; the fix passed its regression and the real round trip.
Revocation was acknowledged, local credentials cleared and the temporary ChatGPT database
deleted afterward. No existing credentials or personal chats were used.

The existing local Ollama 0.40.13 `qwen3.5:9b` also passed streaming, tool/result,
SQLite continuation and Stop through compatible Chat Completions. Its `reasoning` alias
is now preserved under its native wire name alongside support for `reasoning_content`;
the final continuation retained three reasoning-bearing envelopes without repeating the
lookup effect. Temporary harness data was removed; the borrowed Ollama service was not
reconfigured or stopped.

See [provider qualification](provider-qualification.md#live-shared-request-qualification--2026-11-08)
for exact evidence and limits. This is not blanket model/reasoning, forced-refresh, billing,
UI or failure-mode qualification.
