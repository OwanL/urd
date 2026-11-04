# Provider access: qualification proposal

**2026-11-04 — evidence and bounded recommendations, not an implemented integration.**
Scope: direct ChatGPT/Copilot subscriptions, API keys, and one local tool-capable
route. No Pi dependency, Codex/Copilot agent-runtime adoption, hosted Urd credential
broker, or Claude consumer-plan integration. This report selects no application
class hierarchy, project/folder layout, credential store, or model/tool loop.

## Recommendation and owner input

Use **ChatGPT-plan → public OpenAI Responses** as the first representative
integration. It exercises the difficult retained requirements—direct sign-in,
refresh, stateless continuation and native tool-item fidelity—without adopting an
agent runtime. Keep Ollama as the alternative first usable route, not a substitute
for qualifying subscriptions. API-key providers follow; Copilot remains a distinct
access/support gap, not a silently dropped category.

Asked which account to implement first, the owner's exact answer was:
**“This doesnt really matter, either ollama or chatgpt”.** ChatGPT-first is therefore
this stream's sequencing recommendation, not a strong owner preference. No live
sign-in, credential use, model request, charge, or account change was authorized.

## Route matrix

**Evidence levels are separate:** documented interface → implementation evidence →
successful authentication → account/model entitlement → completed inference/tool
round trip. The last three have **not** been established for any Urd route.
Official documentation below was inspected **2026-11-04**; mutable docs are not
version-pinned. Library/source pins are research evidence, not selected packages.

| Route | Documented access and request path | Focused implementation path | Qualification / limitation |
| --- | --- | --- | --- |
| **ChatGPT subscription** | Public-client OAuth/PKCE/OIDC with dynamic registration; bearer token → `POST https://api.openai.com/v1/responses`. Officially documented for open-source/local apps, with preview restrictions [O1–O5]. | OpenAI .NET `ResponsesClient` + MEAI Responses adapter; small route/auth/fidelity glue, not Codex. | Strongest subscription candidate. Account-specific model listing, scope, entitlement, allowance and actual completion still untested. Paid/remotely hosted offerings have a separate OpenAI interest process; a remote UI to one's local backend does not by itself establish eligibility for a different deployment. |
| **GitHub Copilot subscription** | GitHub documents device OAuth and using app tokens with its SDK. Its SDK adopts the Copilot agent runtime, including experimental native in-process transport; neither transport meets Urd's boundary [G1]. | Independent Crush source demonstrates device OAuth → internal Copilot token exchange → OpenAI-compatible model transport [G2–G3]. | Technically evidenced, **not qualified**. No standalone supported subscription inference contract was established in the inspected official SDK/auth/REST docs. Own-app authorization, integration identity, account/model policy and endpoint stability need resolution; source availability is not permission to impersonate VS Code. |
| **OpenAI API key** | Bearer project API key → `POST https://api.openai.com/v1/responses`; SSE and client-executed functions [A1]. | Same official OpenAI/MEAI model-client family, separate credential and route settings. | Documented API access, not included ChatGPT-plan billing. Exact model/project permissions, billing and limits require a key and approved calls. Do not apply generic API-key options to the more restricted plan route. |
| **Anthropic API key** | `POST https://api.anthropic.com/v1/messages`; API key (`x-api-key` remains supported; current docs also document bearer), API-version header; SSE `tool_use` / `tool_result` [A2]. | Official Anthropic C# `AsIChatClient()` for ordinary Messages; native inline-tool beta requires separate fidelity work [L1]. | API-only by owner decision. Qualify chosen SDK/API-version pair, model access and limits. No Claude consumer-plan work. Existing adapter's inline-tool drops/default settings are not resolved by successful authentication. |
| **Local Ollama** | `POST http://localhost:11434/api/chat`; no authentication required for local API; streaming NDJSON, ordinary function tools. Qwen3 is an official tool-calling example [M1]. | OllamaSharp `OllamaApiClient : IChatClient`, after checking actual server/model behavior [L1]. | No model/server installed, inspected or called here. Pin server + model digest/template; confirm it is a local model, not Ollama cloud routing. Tool reliability, thinking replay, call identity and memory requirements remain untested. Ordinary tools-list updates, not assumed OpenAI-native discovery. |

## First route: exact auth → request → tool continuation

This is the current **documented contract**, not a claim that Urd has passed it.

1. **Host and sign-in attempt.** Persist one opaque `ext_agent_host_id` for the
   backend host before first sign-in; a once-generated `urn:uuid:<UUIDv4>` is a
   supported format. Start a loopback listener at
   `http://127.0.0.1:<available-port>/auth/callback`, then open the system browser.
   Keep fresh random state, nonce and S256 PKCE verifier per attempt. The exact
   callback URI is reused in that attempt's code exchange; subsequent attempts
   may change the port, not scheme/host/path. `localhost` is not a substitute [O1].
2. **Authorize.** Open `https://auth.openai.com/api/accounts/authorize` with
   `response_type=code`, redirect URI, state, nonce, PKCE challenge/method,
   `resource=https://api.openai.com/v1`, host ID, and scopes
   `openid profile email offline_access resource.invoke chatgpt.tokens.use.direct`.
   Initial registration uses `client_id=dynamic_agent_client` and
   `agent_name_hint=Urd`; returning sign-in uses the saved **issued** client ID,
   omitting the name hint. A retained ID-token hint is only a sign-in hint [O1].
3. **Callback and exchange.** Validate state even for denial. Initial success must
   return an issued `client_id`; never persist/use `dynamic_agent_client` for token
   exchange. Returning callbacks cannot change the selected registration's ID.
   Form POST `grant_type=authorization_code`, issued client ID, code, verifier,
   exact redirect URI and resource to
   `https://auth.openai.com/api/accounts/oauth/token`; **no client secret** [O1].
4. **Validate, then activate.** Require and validate the ID token's signature,
   issuer `https://auth.openai.com`, audience = issued client ID, expiry and saved
   nonce against OpenAI's discovery/JWKS. Bind verified subject and issued client
   ID together; email is a display hint, not account/workspace identity. Verify
   token-response granted scopes, particularly `chatgpt.tokens.use.direct`, before
   inference. Missing plan scope means signed in but plan access disabled—not a
   reason to silently use paid API credentials. Persist the selected registration,
   tokens, granted scopes and expiry atomically in protected local storage [O1–O3].
5. **Refresh and sign-out.** Serialize refresh per credential set. Near expiry,
   form POST `grant_type=refresh_token`, issued client ID, latest refresh token and
   resource to the same token endpoint; omit scope to retain the grant. Atomically
   replace access token, expiry, scopes and rotating refresh token. Docs specify
   one-hour access tokens and renewable 30-day refresh tokens; honor returned
   timing rather than assuming an eternal login. Terminal refresh-token errors
   require reauthorization; transient failures do not justify erasing credentials.
   Sign-out stops requests, attempts refresh-token revocation using discovery's
   endpoint, then clears local tokens. Report unconfirmed remote revocation;
   retain registration/host identity for later sign-in [O2–O3].
6. **Discover actual choices.** `GET https://api.openai.com/v1/models` with the
   selected access token returns this route's **`models` array**, not assumed
   API-key catalog shape. Display entries with `visibility:"list"` in server
   order, using `display_name`; send `slug` as `model`. Refresh on account switch.
   Listing a model is still not proof an inference will be admitted [O4].
7. **Build one Responses request.** Send bearer access token, `store:false`,
   `stream:true`, and an `input` array carrying required history. Use `instructions`
   or developer messages; explicit `type:"message", role:"system"` is rejected.
   Omit `previous_response_id`; HTTP continuation is locally reconstructed.
   Do not send unsupported knobs such as `max_output_tokens`, temperature/top_p,
   metadata, conversation, background or prompt-cache retention; [O5] owns the
   full exclusion list. Request `include:["reasoning.encrypted_content"]` when
   retaining opaque reasoning for stateless continuation; verify the chosen
   model/route accepts the complete serialized body, not just SDK construction.
8. **Expose and execute native tools.** Function/custom definitions must be grouped
   in namespaces **or** supplied through an ordered input item of the form
   `{"type":"additional_tools","role":"developer","tools":[<function definitions>]}`.
   The latter makes definitions available at that history position [O5–O6].
   Preserve definition/schema/order and returned native function item `id`,
   `call_id`, name, namespace when present, and arguments. Accumulate streamed
   arguments; Urd's execution owner admits/executes the completed call. Append
   `{"type":"function_call_output","call_id":"<same call_id>","output":"<result>"}`
   after the corresponding assistant/native items, then resend the complete input.
   Preserve reasoning item IDs/encrypted content and native item ordering too.
   Exposing a historical definition never authorizes execution by itself.
9. **Complete, or report the actual failure.** Read SSE through
   `response.completed`; neither HTTP 200, text deltas nor clean EOF proves
   success. Treat `response.failed`, `response.incomplete`, explicit error events,
   cancellation and truncation distinctly. A quota failure can arrive after the
   stream opens. Keep status, structured error code/parameter and request ID,
   without credentials; some admission errors have only a `detail` body [O4–O5].

**Unavailable on this plan route:** Responses `tool_search`, even client-executed;
image generation, file search, Code Interpreter, native computer use and hosted
MCP/connectors; Files upload and transcription APIs; audio/video input. Local
shell/MCP tools exposed as permitted functions are a different mechanism.
Text/image/file inputs depend on the chosen model. None of these limits implies
that the general API-key Responses API lacks those features [O5].

## Reuse only where it helps

- **Model calls:** qualify OpenAI .NET **2.14.0** + MEAI **10.11.0** as a focused
  starting pair, not a fresh HTTP/SSE implementation. The Responses adapter is
  experimental. Urd still needs route-required settings, access-token refresh
  integration and native-item persistence/terminal checks. Ordinary MEAI
  `ChatOptions.Tools` alone does not establish the required namespace/additional
  item wire shape. Execution Design owns the loop/fidelity comparison [L1–L2].
- **OAuth:** Duende.IdentityModel.OidcClient **7.1.1**, source
  `ab00474eb5311c3b4431b992e7296e779f5904aa`, is an Apache-2.0 focused candidate,
  not an approved dependency. Its manual flow supplies PKCE/state and code
  exchange. Source reads the current options' client ID at exchange, making the
  dynamic-issued-ID handoff feasible with isolated per-attempt options [L3].
  **It is not plug-and-play:** inspected code does not generate/check nonce;
  signature requirement defaults false and its fallback merely decodes claims.
  A missing ID token is accepted by the generic flow. Supply maintained JWT/JWKS
  validation, require the token, enforce nonce/identity/scopes, validate state on
  error callbacks, and handle issued-ID/resource parameters. Do not write crypto
  or weaken OpenAI's validation contract to fit defaults. The offline follow-up
  below now exercises these mechanics; it does not qualify live authentication.
- **Copilot:** independent source exchanges a GitHub OAuth token using
  `GET https://api.github.com/copilot_internal/v2/token`, bearer auth, for a
  short-lived Copilot token. “Refresh” repeats that exchange; its so-called
  refresh credential is the GitHub token, not an OAuth refresh token. Catwalk's
  generator uses returned `endpoints.api`, `/models` and `supported_endpoints`
  to select `/responses` versus `/chat/completions`; Crush itself uses its catalog
  rather than proving fresh per-user discovery. Fixed VS Code/Copilot integration
  headers and another app's OAuth client ID are compatibility evidence, **not
  defaults approved for Urd**. Reuse ordinary transport only after resolving this
  route-specific authorization/support gap; do not adopt the full runtime [G2–G3].
- **Local correction:** current Ollama streaming-tool guidance explicitly says
  replay `thinking`, `content` and `tool_calls`. The older research's “no Ollama
  input slot” explanation is not a safe current server claim. Pinned OllamaSharp's
  reported thinking drop/synthetic IDs remain adapter issues to recheck, not
  proof that local models cannot support coherent continuation [M1, L1].

## Minimal responsibilities and portability

These are behaviors, **not proposed classes or filesystem boundaries**:

- **Account connection:** selected provider/route, endpoint, verified identity and
  registration, user-facing label and enabled model choices. Keep ChatGPT
  registrations separate even when emails match. Authentication, scope granted,
  catalog retrieval and completed inference are distinct statuses.
- **Credential ownership:** backend-local storage, per-credential refresh
  serialization, atomic replacement, sign-out/revocation and redaction. No token
  in browser storage, transcripts, usage analytics or source control; no broker
  and no import of Pie sign-ins. A model request snapshots its selected connection;
  changing the UI account must not splice another account's token into that run.
- **Provider boundary:** obtain a current credential, enforce exact route/model
  settings, preserve native context/usage/errors, and report completion correctly.
  Selection, allowed fallbacks and tool execution remain separate responsibilities.
  Never infer “subscription” means free/unlimited, or silently enable another
  billing route; ChatGPT app limits/credits and shared plan usage still apply.
- **Windows → macOS → Linux:** protected local files are documented by OpenAI;
  Unix `0600` is not a Windows ACL. Decide native protection/credential-store
  integration during implementation, without requiring a Windows-only facility
  on every OS. Keychain/Secret Service availability and background/headless access
  need tests if selected. Same-host loopback/browser launch and occupied-port
  handling need OS qualification.
- **Remote browser caveat:** the OAuth callback returns to `127.0.0.1` on the
  browser's device. A phone/LAN browser cannot simply complete the backend's
  local callback. Initial same-machine sign-in is the documented baseline;
  remote onboarding needs a separately qualified flow, not an invented LAN
  redirect. Ordinary remote use after backend sign-in is a separate question.
  Browser Design has this dependency; no remote-auth design is selected here.

## Verification and next qualification sequence

**Initial research performed read-only public documentation/source checks.** It
made no authenticated requests and ran no live/local model, credential probe or build.
The separately authorized offline follow-up below adds synthetic executable evidence.
Independent source checks covered Copilot, API-key/local routes and the focused
OAuth candidate. Direct OpenAI docs and public OIDC discovery were read; they
establish endpoints/contracts, not deployment or account eligibility.

Execution Design separately exercised the real pinned OpenAI/MEAI adapter against
local synthetic HTTP/SSE. Its current findings: ordered `additional_tools` survives
with an assistant MEAI carrier (native role remains developer); developer-role raw
content drops; ordinary JSON loses raw provider items, explicit reconstruction
restores them; function `call_id` survives but the native item ID must be captured
from raw streaming events before coalescing; encrypted reasoning/ID survives;
errors are not necessarily exceptions. These are **mock transport mechanics**, not
OpenAI acceptance or entitlement. Final fixture evidence belongs in
[EXECUTION-DESIGN.md](EXECUTION-DESIGN.md), not duplicated provider test claims.

1. **Before login:** finish fixture coverage for the restricted plan request,
   ordered native items, malformed/failed/incomplete/truncated streams, cancellation
   and restart reconstruction. Synthetic OAuth dynamic client-ID exchange,
   signed-token validation failures and denial/missing scope checks are now
   covered by the offline follow-up below. Same-account refresh
   races, replacement persistence and revocation failure remain untested. These
   are bounded qualification steps, not an auth framework.
2. **Owner-approved account test:** fresh Urd-only registration, verify identity
   and scopes, then retrieve that account's catalog. Ask specifically before
   launching sign-in or storing credentials. Do not read existing Pie tokens.
3. **Separately bounded inference approval:** choose a returned model; approve
   a short text request and a two-request harmless function/result round trip,
   no automatic retries/fallbacks. State allowance/credit implications up front.
   The route rejects `max_output_tokens`, so that field cannot be promised as a
   spending cap. Observe actual terminal completion and usage; then qualify
   refresh, reauthorization and native append with the same account.
4. **Broaden deliberately:** API-key OpenAI/Anthropic using their own documented
   settings and approved billing; Ollama using an explicitly local tool-capable
   model/server under the shared heavy-work lease. Do not download/run a model
   opportunistically on this memory-constrained host. Repeat the meaningful
   tool/continuation/cancellation tests, not a huge SDK matrix.
5. **Copilot decision gate:** first establish Urd-appropriate app/integration
   authorization or obtain provider confirmation. If only an internal-compatible
   route remains, present the concrete support/maintenance tradeoff to the owner
   before implementing or signing in. Success with one account/model cannot
   establish every plan, model, enterprise policy or geographic entitlement.

**Initial scaffold is not blocked by live access. Normal use is not yet qualified.**
Remaining normal-use gates: at least one real account/model/tool round trip,
credential renewal/revocation, durable native context, chosen-platform onboarding,
and bounded failure handling. Copilot support remains its own gap. There is no
outstanding owner question in this session and no production work is authorized
by this report.

## Offline auth follow-up — 2026-11-04

**Authorized:** the owner's “Yes—start both” covered this bounded fake-provider
experiment, not live sign-in or a production dependency choice. **Result: candidate
mechanics fit with explicit glue; defaults alone do not meet the route contract.**

Ran the real **Duende.IdentityModel.OidcClient 7.1.1** package plus maintained
**Microsoft.IdentityModel.JsonWebTokens 8.23.0** (Microsoft's active 8.x line;
MIT, source `8b16f418b3d587416d293e9ba34c5d310781cdb3` [L4]). Windows x64, .NET SDK
10.0.403/runtime 10.0.14. The 35-case executable uses an in-process
`HttpMessageHandler` for fake discovery, authorization, token and JWKS endpoints
on reserved `.invalid` domains; no socket-backed handler exists. Per-case RSA-2048
keys are generated/disposed in memory. Real Duende constructs authorization/code
requests, fetches discovery/JWKS and invokes the supplied validator; Microsoft's
JWT library verifies signatures/issuer/audience/lifetime using those public keys.
No crypto implementation or production auth classes were introduced.

| Executed check | Finding / necessary glue |
| --- | --- |
| New and returning registration | `dynamic_agent_client` authorization → issued ID in code POST and ID-token audience works by changing **isolated attempt options** before `ProcessResponseAsync`. Without handoff, the fake rejects exchange. Reject missing/placeholder issued IDs and returning-ID substitution before exchange; returning callback may omit ID. |
| PKCE and parameters | Real S256 challenge matches generated verifier; fake token endpoint rejects altered verifier. Exact redirect URI/resource, form encoding and absence of client secret/Authorization header checked. Returning authorization omits the registration name hint. |
| State, including denial | Wrong/missing state rejected before exchange; valid denial exchanges no code. Control confirms Duende checks success state but returns `access_denied` without checking denial state. Pre-check **all** callbacks before using error or issued ID. |
| Required signed identity | Valid signed token succeeds. Unsigned/wrong-key, wrong issuer/audience, expired/no-expiry, wrong/missing nonce and missing subject are rejected. Maintained JWT validation handles crypto and standard claims; attempt-bound nonce/subject checks are explicit. Missing ID token needs a separate presence gate: even `RequireIdentityTokenSignature=true` does not require a token or invoke the validator when absent. |
| Unsafe-default controls | Executed controls accept missing/unsigned ID tokens and a wrong nonce; authorization generates no nonce by default. Supply a fresh nonce and validator rather than treating generic OAuth success as verified OIDC identity. |
| Granted scopes | Missing plan/invoke scope, absent token-response scope, callback-only scope and substring lookalikes leave valid identity **signed in, plan use disabled**. Check exact token-response grants; never infer access from requested/callback scopes or silently fall back to API billing. |
| Small refresh check | Issued ID/resource, omitted scope, rotated access/refresh tokens, returned expiry, next use of replacement and stale-token `invalid_grant` all exercised. **New seam:** `RefreshTokenAsync` returns neither granted scope nor raw `TokenResponse`, even when fake refresh narrows scope. Use a scope-preserving lower-level Duende refresh response (plus applicable validation) or another verified seam; do not silently retain obsolete grants. That alternative was not implemented here. |

**Evidence:** `C:\Users\OwanLazic\AppData\Local\Temp\urd-foundation-streams-6268b517\offline-auth-b3a39851\`
contains `Program.cs`, `AuthProbe.csproj`, `packages.lock.json`, `results.json`,
`build.log`, `run.log`, reproduction notes and pinned/public source snapshots.
Final build: **0 warnings/errors; 35/35 cases passed**, including controls that
intentionally demonstrate unsafe defaults. Fixtures/logs are temporary local
artifacts, not committed regression infrastructure. Independent acceptance review
covers fixture assertions and the distinction between library behavior and glue.

**Recommendation:** retain this as a viable candidate, not a selected dependency.
The code-flow handoff is not a blocker; required glue is explicit callback ordering,
per-attempt ID/nonce state, maintained signature/claim validation, mandatory identity
and granted-scope gating. Refresh scope propagation needs attention before adoption.
The bounded fixture is not an estimate of production implementation cost.

**Not established:** real OpenAI registration, actual signing algorithms/key rollover,
JWKS cache/network failures, account/model entitlement or inference; browser/loopback
listener behavior, concurrent sign-ins, refresh serialization/races, durable atomic
replacement, refresh ID-token handling, revocation, cancellation/retry policy or
platform secret protection. No real endpoints for authentication/inference, personal
configuration, credentials, browser or model calls were used. No owner decision is
pending; production auth structure and live-account approval remain separate gates.

## Sources

All live documents accessed 2026-11-04; pinned source links fix the inspected code.

- **L4:** Microsoft [JWT 8.23.0 package/source metadata](https://api.nuget.org/v3-flatcontainer/microsoft.identitymodel.jsonwebtokens/8.23.0/microsoft.identitymodel.jsonwebtokens.nuspec), [8.x support status](https://github.com/AzureAD/azure-activedirectory-identitymodel-extensions-for-dotnet#versions).

- **O1:** OpenAI [OSS/local overview](https://developers.openai.com/siwc/token-sharing-open-source), [registration/sign-in](https://developers.openai.com/siwc/token-sharing-open-source/sign-in).
- **O2:** [Accounts, refresh, revocation and credential handling](https://developers.openai.com/siwc/token-sharing-open-source/profiles-and-sessions), [token lifetimes](https://developers.openai.com/siwc/token-sharing-open-source/token-reference).
- **O3:** [Errors/recovery](https://developers.openai.com/siwc/token-sharing-open-source/errors-and-recovery), [public OIDC discovery](https://auth.openai.com/.well-known/openid-configuration).
- **O4:** [Account-specific models and inference](https://developers.openai.com/siwc/token-sharing-open-source/models-and-inference).
- **O5:** [Plan preview restrictions](https://developers.openai.com/siwc/token-sharing-open-source/preview-limitations).
- **O6:** [Function tools/namespaces](https://developers.openai.com/api/docs/guides/function-calling), [additional_tools position contract](https://developers.openai.com/api/docs/guides/tools-tool-search#add-tools-at-a-specific-point-in-the-input). The latter's general `tool_search` examples are **not** supported by the plan route.
- **G1:** GitHub [device OAuth](https://docs.github.com/en/apps/oauth-apps/building-oauth-apps/authorizing-oauth-apps#device-flow); Copilot SDK pin `d28a13687fbe122c14e81fbb9cd01725e051bb14` [architecture](https://github.com/github/copilot-sdk/blob/d28a13687fbe122c14e81fbb9cd01725e051bb14/README.md#architecture), [app OAuth integration](https://github.com/github/copilot-sdk/blob/d28a13687fbe122c14e81fbb9cd01725e051bb14/docs/setup/github-oauth.md); [Copilot REST index](https://docs.github.com/en/rest/copilot), [terms](https://docs.github.com/en/site-policy/github-terms/github-terms-for-additional-products-and-features#github-copilot), [model pricing](https://docs.github.com/en/copilot/reference/copilot-billing/models-and-pricing).
- **G2:** Crush pin `d391cee20d1942794aec5ea6faff07170444211f`: [device/exchange implementation](https://github.com/charmbracelet/crush/blob/d391cee20d1942794aec5ea6faff07170444211f/internal/oauth/copilot/oauth.go), [headers](https://github.com/charmbracelet/crush/blob/d391cee20d1942794aec5ea6faff07170444211f/internal/oauth/copilot/http.go), [inference setup](https://github.com/charmbracelet/crush/blob/d391cee20d1942794aec5ea6faff07170444211f/internal/agent/coordinator.go#L1186-L1238).
- **G3:** Catwalk pin `ccecdc1afd2d21f8bf6b07367913ae483f02f195`: [endpoint/model catalog generator](https://github.com/charmbracelet/catwalk/blob/ccecdc1afd2d21f8bf6b07367913ae483f02f195/cmd/copilot/main.go#L154-L269).
- **A1:** OpenAI [API authentication](https://developers.openai.com/api/reference/overview), [Responses streaming](https://developers.openai.com/api/docs/guides/streaming-responses), functions in O6.
- **A2:** Anthropic [API auth/base URL](https://platform.claude.com/docs/en/api/overview), [versioning](https://platform.claude.com/docs/en/api/versioning), [Messages streaming](https://platform.claude.com/docs/en/build-with-claude/streaming), [tools](https://platform.claude.com/docs/en/agents-and-tools/tool-use/overview).
- **M1:** Ollama [local/cloud authentication](https://docs.ollama.com/api/authentication), [chat API](https://docs.ollama.com/api/chat), [streaming/tool continuation](https://docs.ollama.com/capabilities/tool-calling), [Qwen3:8b](https://ollama.com/library/qwen3:8b).
- **L1:** [MODEL-INTERFACE-RESEARCH.md](MODEL-INTERFACE-RESEARCH.md) for pinned OpenAI/Anthropic/OllamaSharp adapters, with the Ollama correction above.
- **L2:** MEAI [Responses adapter at v10.11.0](https://github.com/dotnet/extensions/blob/25f07afa51034e37b334210642d7dc5f191c142d/src/Libraries/Microsoft.Extensions.AI.OpenAI/OpenAIResponsesChatClient.cs); [OpenAI .NET 2.14.0 source](https://github.com/openai/openai-dotnet/tree/2e77b08828145f658ec04e49aec87abb1543c553).
- **L3:** Duende [7.1.1 package/source metadata](https://api.nuget.org/v3-flatcontainer/duende.identitymodel.oidcclient/7.1.1/duende.identitymodel.oidcclient.nuspec), [manual flow](https://docs.duendesoftware.com/identitymodel-oidcclient/manual/); pinned [AuthorizeClient](https://github.com/DuendeSoftware/foss/blob/ab00474eb5311c3b4431b992e7296e779f5904aa/identity-model-oidc-client/src/IdentityModel.OidcClient/AuthorizeClient.cs), [ResponseProcessor](https://github.com/DuendeSoftware/foss/blob/ab00474eb5311c3b4431b992e7296e779f5904aa/identity-model-oidc-client/src/IdentityModel.OidcClient/ResponseProcessor.cs), [Policy](https://github.com/DuendeSoftware/foss/blob/ab00474eb5311c3b4431b992e7296e779f5904aa/identity-model-oidc-client/src/IdentityModel.OidcClient/Policy.cs), [decode-only fallback](https://github.com/DuendeSoftware/foss/blob/ab00474eb5311c3b4431b992e7296e779f5904aa/identity-model-oidc-client/src/IdentityModel.OidcClient/NoValidationIdentityTokenValidator.cs), [Apache-2.0 license](https://github.com/DuendeSoftware/foss/blob/ab00474eb5311c3b4431b992e7296e779f5904aa/LICENSE).
