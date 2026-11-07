# Headless provider management — initial design

2026-11-08. Scope and connection shape approved by the owner; remaining operations
are proposals until reviewed. No provider implementation or live account activity is
claimed. [Provider qualification](../PROVIDER-QUALIFICATION.md) owns route research
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

## Proposed configuration operations — not yet approved

One Core `ProviderManager`, exposed through the existing local HTTP Server. No second
CLI, provider UI, generic configuration framework or JSON/YAML hot-reload mechanism.

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

The proposed configuration boundary passed independent design review. Authentication,
model discovery, explicit access checks and credential persistence are subsequent bites;
full session/provider integration, tools and provider UI remain outside this slice.
