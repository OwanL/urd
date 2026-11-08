# Headless provider management — initial design

2026-11-08. Scope, connection shape and configuration operations approved by the
owner. The manager owns credential management; the owner selected plaintext storage
for the initial version rather than password/OS-key management. The authentication
operation surface and flow are also approved; detailed result types, model operations
and persistence integration remain proposals until reviewed. No provider implementation
or live account activity is claimed. [Provider qualification](../PROVIDER-QUALIFICATION.md) owns route research
and the bounded initial provider set.

## Approved connection view

One saved connection is one independently configured route to a provider. Its stable
ID, not its display name or provider name, owns its credentials. Multiple connections
to the same provider are representable without sharing an account accidentally.

```csharp
public sealed record ProviderConnection(
  Guid Id,
  string Name,
  ProviderRoute Route,
  LlmApi Api,
  Uri Endpoint,
  AuthenticationMethod Authentication);

public enum ProviderRoute { OpenAI, ChatGpt, Custom }
public enum LlmApi { OpenAIResponses, OpenAIChatCompletions }
public enum AuthenticationMethod { None, ApiKey, OAuth }
```

This immutable, secret-free read view is not an unrestricted mutation request. OpenAI
initially resolves to its official Responses endpoint and API-key authentication;
ChatGPT uses its separately qualified subscription route and OAuth. Official defaults
are supplied by the integration, not arbitrary editable fields. Custom connections
select a supported protocol and endpoint with API-key or no authentication; arbitrary
OAuth registrations are outside this slice. Exact URL defaults await route verification.

Credentials stay backend-private and keyed by connection ID. Models and authentication
status are separate observations; saving a connection proves neither access nor model
selection. Changing an endpoint must not silently redirect an existing credential.
The connection-view proposal passed independent design review before owner approval.

## Approved configuration operations

One Core `ProviderManager`, exposed through the existing local HTTP Server. It owns
the whole connection-management lifecycle, including credential storage coordination,
not configuration alone. No second CLI, provider UI, generic configuration framework
or JSON/YAML hot-reload mechanism.

```csharp
Task<IReadOnlyList<ProviderConnection>> ListAsync(CancellationToken cancellationToken = default);
Task<ProviderConnection> GetAsync(Guid connectionId, CancellationToken cancellationToken = default);
Task<ProviderConnection> AddOpenAIAsync(string name);
Task<ProviderConnection> AddChatGptAsync(string name);
Task<ProviderConnection> AddCustomAsync(CustomProviderSettings settings);
Task<ProviderConnection> RenameAsync(Guid connectionId, string name);
Task<ProviderConnection> UpdateCustomAsync(Guid connectionId, CustomProviderSettings settings);
Task RemoveAsync(Guid connectionId);

public sealed record CustomProviderSettings(
  string Name,
  LlmApi Api,
  Uri Endpoint,
  AuthenticationMethod Authentication);
```

These are signatures, not an interface or extra abstraction. `CustomProviderSettings`
permits only `None` or `ApiKey`; updating a non-Custom connection is rejected without
mutation. Reads are offline and additions save configuration only. No constructor,
read or add implicitly starts sign-in, discovery or inference. Name changes preserve
credentials. Commands serialize through the owning manager; a custom update replaces
its explicitly supplied settings, not unrelated state from a stale read view.

When credentials/discovery exist, changing endpoint, protocol or authentication must
clear the previous credential and discovered-model state and invalidate in-flight
operations. Late completions cannot restore superseded state. Credential attachment
must check that the intended connection configuration is still current. The precise
storage transaction and operation-lifetime design remains a separate review, not an
implemented guarantee. Removing a connection deletes its local state; remote token
revocation belongs to an explicit disconnect operation and is not implied by removal.
Neither operation cancels the user's paid subscription.

The existing Server's local admission checks, no-store behavior and separation between
read cancellation and admitted mutations continue to apply. Server-generated IDs and
no automatic mutation retries keep an unacknowledged creation explicitly uncertain;
listing connections is not proof of which creation lost its acknowledgement. Exact
HTTP routes/statuses, input validation and persistence-failure semantics still require
review before implementation. There are no live agent consumers in this slice.

The configuration boundary passed independent design review and owner approval.
Authentication, model discovery, explicit access checks and credential persistence are
subsequent bites; full session/provider integration, tools and provider UI remain outside
this slice.

## Credential ownership

`ProviderManager` is the single public management entry point for connection settings,
API-key replacement, subscription sign-in, refresh, disconnect and credential removal.
Callers do not coordinate a configuration store with a separate public credential API.
Internal storage handles persistence; provider-specific authentication code handles
each route's sign-in and refresh protocol. Exact types and method signatures remain
unselected; the initial plaintext choice is recorded below.

The manager must coordinate changes across these responsibilities: changing a destination
cannot forward an old credential, and late sign-in/refresh completions cannot restore
credentials after disconnect, removal or superseding configuration changes. Public reads
report status, never credential contents. These are design requirements, not implemented
or tested guarantees.

## Approved authentication operations and flow

All operations belong to `ProviderManager`; the signatures below show the approved
operation shape, not finalized result types or transport DTOs.

```csharp
SetApiKeyAsync(connection, apiKey)
GetCredentialStatusAsync(connectionId)
BeginSignInAsync(connectionId)
GetSignInAsync(signInId)
CancelSignInAsync(signInId)
DisconnectAsync(connectionId)
```

- API-key replacement saves the key without implicit provider calls. The caller supplies
  its previously read `ProviderConnection`; under the manager guard, compare ID, route,
  API, endpoint and authentication before saving. Ignore display-name differences. Reject
  a changed destination rather than attaching the key to it. No public revision field
  is needed solely for this check. Status reads are offline and never return a key/token.
- ChatGPT sign-in returns an attempt ID, provider authorization URL and expiry. The caller
  opens the URL in a browser on the backend machine; Urd does not launch it automatically.
  The local callback is validated before committing credentials. Polling/cancellation use
  the attempt ID, so cancelling an old attempt cannot cancel its replacement. There is no
  manual token-copy step, provider settings UI or arbitrary OAuth registration capability.
- Repeated begin while an attempt is pending returns that attempt. New attempts use fresh
  OAuth state/nonce/PKCE; completed attempts are not replayed. Pending attempts are ephemeral,
  expire and do not resume after restart. Failure/cancellation preserves prior credentials;
  successful replacement is guarded against concurrent disconnect/removal/configuration
  changes. Network work runs outside the mutation guard.
- Returning sign-in reauthorizes the saved registration and verifies the same account
  identity/client mapping. A different account uses a separate connection. Authorization
  URLs returned headlessly omit the optional `id_token_hint` so no saved ID token is exposed.
  Status distinguishes stored credentials from access qualification; refreshable token
  expiry is not automatically reauthentication, and validated identity alone is not a plan
  access grant. Exact status/result records remain an implementation-design detail.
- Refresh is automatic when required by an explicit provider operation, serialized per
  connection, not driven by a background timer or status reads. Late refresh results cannot
  overwrite newer credentials, resurrect removed connections or reverse a disconnect.
- Disconnect fences credential use and invalidates pending connection operations. Join
  owned refresh before capturing the latest token, attempt bounded provider revocation,
  then clear local tokens even if remote revocation remains unconfirmed. Report that
  distinction; local persistence failure is not successful disconnect. Keep the connection
  and account/client registration for later sign-in, but no retained ID/access/refresh
  tokens. Interrupted disconnect must not silently reopen credential use; exact durable
  operation state and shutdown joins need the storage/lifetime integration review.

The operation proposal and its token-exposure/disconnect corrections passed independent
review before owner approval. OpenAI's public registration/sign-in and accounts/sessions
pages were rechecked on 2026-11-08; see O1/O2 in [provider qualification](../PROVIDER-QUALIFICATION.md).
This is design evidence only, not live authentication or adapter qualification.

## Initial credential storage — plaintext

After comparing OS protection, OS credential stores and a portable password-protected
vault, the owner chose to start with plaintext and potentially revisit encryption later.
The priority is simple startup and small implementation, without a Urd unlock password,
user-managed keys or a new encryption framework. Encryption is not promised follow-up work.

The proposed persistence location is the existing `urd.sqlite`, keeping credential writes
and connection changes within one database transaction. This does not authorize a broad
storage refactor; the shared database owner and exact schema still need integration design.
No separate public credential service or speculative encryption abstraction is needed.

Credentials remain backend-private: public reads return status, never secrets; keys and
tokens must not enter logs or distributable source/build outputs. Existing `.gitignore`
entries exclude `/data/` and SQLite files including journals/WAL sidecars. Ignore rules
reduce accidental inclusion but do not stop forced adds, renamed exports or copied data.

**Explicit limitation:** plaintext does not meet the earlier goal that accidental Git
inclusion would leave credentials unusable. Anyone obtaining the credential data can use
the secrets. Later encryption cannot retroactively protect earlier copies, backups or
commits. This is an initial simplicity trade-off, not a claim of encrypted-at-rest security.
