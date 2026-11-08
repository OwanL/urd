using System.Diagnostics.CodeAnalysis;
using Urd.Core.Sessions;
using Urd.Core.Storage;

namespace Urd.Core.Providers;

// Owns saved-provider operations and joins its network work before borrowed storage can be disposed.
public sealed class ProviderManager : IDisposable {
  private static readonly Uri OpenAIEndpoint = new("https://api.openai.com/v1/");
  private readonly ProviderStorage storage;
  private readonly AsyncLock gate = new();
  private readonly HttpMessageHandler handler;
  private readonly HttpClient client;
  private readonly ChatGptAuthentication authentication;
  private readonly OpenAIProviderClient models;
  private readonly ProviderSignIns signIns;
  private readonly List<ProviderRequest> requests = [];
  private readonly Dictionary<Guid, Task<ProviderState>> refreshes = [];
  private readonly Dictionary<Guid, Task<ProviderDisconnectResult>> disconnects = [];
  private readonly Dictionary<Guid, long> fenced = [];
  private Task? shutdown;
  private bool stopping, disposed;

  public ProviderManager(UrdStore store, Func<Uri> callbackUri)
      : this(store, callbackUri, new SocketsHttpHandler { AllowAutoRedirect = false }) { }

  // The manager owns this handler; test transports cannot fall back to real network access.
  internal ProviderManager(UrdStore store, Func<Uri> callbackUri, HttpMessageHandler handler, Uri? issuer = null) {
    ArgumentNullException.ThrowIfNull(store);
    ArgumentNullException.ThrowIfNull(callbackUri);
    ArgumentNullException.ThrowIfNull(handler);
    storage = store.Providers;
    this.handler = handler;
    client = new(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
    authentication = new(handler, issuer);
    models = new(client);
    signIns = new(storage, authentication, gate, callbackUri, CancelRequests);
  }

  public async Task<IReadOnlyList<ProviderConnection>> ListAsync(CancellationToken cancellationToken = default) {
    using var held = await gate.AcquireAsync(cancellationToken);
    ObjectDisposedException.ThrowIf(disposed, this);
    return storage.List();
  }

  public async Task<ProviderConnection> GetAsync(Guid connectionId, CancellationToken cancellationToken = default) {
    using var held = await gate.AcquireAsync(cancellationToken);
    ObjectDisposedException.ThrowIf(disposed, this);
    return storage.Get(connectionId).Connection;
  }

  public Task<ProviderConnection> AddOpenAIAsync(string name) => AddAsync(new(Guid.NewGuid(), ValidateName(name),
      ProviderRoute.OpenAI, LlmApi.OpenAIResponses, OpenAIEndpoint, AuthenticationMethod.ApiKey));

  public Task<ProviderConnection> AddChatGptAsync(string name) => AddAsync(new(Guid.NewGuid(), ValidateName(name),
      ProviderRoute.ChatGpt, LlmApi.OpenAIResponses, OpenAIEndpoint, AuthenticationMethod.OAuth));

  public Task<ProviderConnection> AddCustomAsync(CustomProviderSettings settings) {
    settings = ValidateCustom(settings);
    return AddAsync(new(Guid.NewGuid(), settings.Name, ProviderRoute.Custom,
        settings.Api, settings.Endpoint, settings.Authentication));
  }

  public async Task<ProviderConnection> RenameAsync(Guid connectionId, string name) {
    name = ValidateName(name);
    using var held = await gate.AcquireAsync();
    ThrowIfStopping();
    return storage.Rename(connectionId, name).Connection;
  }

  public async Task<ProviderConnection> UpdateCustomAsync(Guid connectionId, CustomProviderSettings settings) {
    settings = ValidateCustom(settings);
    ProviderState updated;
    Task cancelled;
    using (await gate.AcquireAsync()) {
      ThrowIfStopping();
      ProviderState previous = storage.Get(connectionId);
      if (previous.Disconnecting)
        throw new ProviderOperationException(ProviderFailure.Disconnecting);
      updated = storage.UpdateCustom(connectionId, settings);
      cancelled = updated.Generation != previous.Generation ? CancelRequests(connectionId) : Task.CompletedTask;
      if (updated.Generation != previous.Generation)
        fenced.Remove(connectionId);
    }
    await cancelled;
    return updated.Connection;
  }

  public async Task RemoveAsync(Guid connectionId) {
    Task cancelled;
    using (await gate.AcquireAsync()) {
      ThrowIfStopping();
      if (disconnects.ContainsKey(connectionId))
        throw new ProviderOperationException(ProviderFailure.Disconnecting);
      storage.Remove(connectionId);
      fenced.Remove(connectionId);
      cancelled = Task.WhenAll(CancelRequests(connectionId), signIns.CancelConnection(connectionId),
          ObserveCompletion(refreshes.GetValueOrDefault(connectionId)));
    }
    await cancelled;
    using var held = await gate.AcquireAsync();
    signIns.Forget(connectionId);
    fenced.Remove(connectionId);
  }

  public async Task<ProviderCredentialStatus> SetApiKeyAsync(ProviderConnection connection, string apiKey) {
    ArgumentNullException.ThrowIfNull(connection);
    if (string.IsNullOrWhiteSpace(apiKey) || apiKey.Length > 16 * 1024 || apiKey.Contains('\r') || apiKey.Contains('\n'))
      throw new ArgumentException("An API key must be nonempty and fit within 16 KiB.", nameof(apiKey));
    Task cancelled;
    ProviderCredentialStatus status;
    using (await gate.AcquireAsync()) {
      ThrowIfStopping();
      ProviderState state = storage.Get(connection.Id);
      if (!SameBinding(state.Connection, connection))
        throw new ProviderOperationException(ProviderFailure.ConnectionChanged);
      if (state.Connection.Authentication != AuthenticationMethod.ApiKey)
        throw new ArgumentException("This connection does not use an API key.", nameof(connection));
      ProviderState updated = storage.SetCredentials(connection.Id, state.Generation, null,
          new(apiKey, null, null, null, null, []));
      fenced.Remove(connection.Id);
      status = CredentialStatus(updated);
      cancelled = CancelRequests(connection.Id);
    }
    await cancelled;
    return status;
  }

  public async Task<ProviderCredentialStatus> GetCredentialStatusAsync(
      Guid connectionId, CancellationToken cancellationToken = default) {
    using var held = await gate.AcquireAsync(cancellationToken);
    ObjectDisposedException.ThrowIf(disposed, this);
    return CredentialStatus(storage.Get(connectionId));
  }

  public Task<ProviderSignIn> BeginSignInAsync(Guid connectionId) => signIns.BeginAsync(connectionId);
  public Task<ProviderSignIn> GetSignInAsync(Guid signInId, CancellationToken cancellationToken = default) =>
      signIns.GetAsync(signInId, cancellationToken);
  public Task<ProviderSignIn> CancelSignInAsync(Guid signInId) => signIns.CancelAsync(signInId);
  public Task<ProviderSignIn> CompleteSignInAsync(string state, string callbackUrl) =>
      signIns.CompleteAsync(state, callbackUrl);

  public async Task<ProviderDisconnectResult> DisconnectAsync(Guid connectionId) {
    Task<ProviderDisconnectResult> result;
    TaskCompletionSource<ProviderDisconnectResult>? completion = null;
    Task joined = Task.CompletedTask;
    using (await gate.AcquireAsync()) {
      ThrowIfStopping();
      if (!disconnects.TryGetValue(connectionId, out result!)) {
        storage.BeginDisconnect(connectionId);
        completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        result = completion.Task;
        disconnects.Add(connectionId, result);
        joined = Task.WhenAll(CancelRequests(connectionId), signIns.CancelConnection(connectionId),
            ObserveCompletion(refreshes.GetValueOrDefault(connectionId)));
      }
    }
    if (completion is not null)
      _ = DisconnectAsync(connectionId, joined, completion);
    return await result;
  }

  public async Task<ProviderModelCatalog> GetModelCatalogAsync(
      Guid connectionId, CancellationToken cancellationToken = default) {
    using var held = await gate.AcquireAsync(cancellationToken);
    ObjectDisposedException.ThrowIf(disposed, this);
    return storage.Get(connectionId).Catalog ?? new(connectionId, null, []);
  }

  public Task<ProviderModelCatalog> RefreshModelCatalogAsync(Guid connectionId) => RunRequestAsync(connectionId,
      async (state, token) => {
        IReadOnlyList<ProviderModel> fetched = await models.GetModelsAsync(state.Connection, state.Credentials, token);
        var catalog = new ProviderModelCatalog(connectionId, DateTimeOffset.UtcNow, fetched);
        using var held = await gate.AcquireAsync(token);
        token.ThrowIfCancellationRequested();
        RequireCurrentGrant(state);
        if (!storage.SaveCatalog(connectionId, state.Generation, catalog))
          throw new ProviderOperationException(ProviderFailure.ConnectionChanged);
        return catalog;
      }, CancellationToken.None);

  public Task<ProviderModelTestResult> TestModelAsync(Guid connectionId, string modelId,
      CancellationToken cancellationToken = default) {
    if (string.IsNullOrWhiteSpace(modelId) || modelId.Length > 256 || modelId.Any(char.IsControl))
      throw new ArgumentException("A model ID must be nonempty and at most 256 characters.", nameof(modelId));
    return RunRequestAsync(connectionId,
        (state, token) => models.TestModelAsync(state.Connection, state.Credentials, modelId, token), cancellationToken);
  }

  public async Task ShutdownAsync() {
    Task joined;
    using (await gate.AcquireAsync()) {
      if (shutdown is null) {
        stopping = true;
        shutdown = Task.WhenAll(signIns.Shutdown(),
            Task.WhenAll(requests.Select(request => Task.WhenAll(request.Cancellation.CancelAsync(), request.Finished.Task))),
            Task.WhenAll(refreshes.Values.Select(task => ObserveCompletion(task))),
            Task.WhenAll(disconnects.Values.Select(task => ObserveCompletion(task))));
      }
      joined = shutdown;
    }
    await joined;
  }

  [SuppressMessage("Major Code Smell", "S3877", Justification =
      "Disposing an active manager would release transport before its owned asynchronous work has drained.")]
  public void Dispose() {
    if (disposed)
      return;
    if (shutdown is not { IsCompleted: true })
      throw new InvalidOperationException("Provider operations must finish shutdown before disposal.");
    client.Dispose();
    handler.Dispose();
    disposed = true;
  }

  private async Task<ProviderConnection> AddAsync(ProviderConnection connection) {
    using var held = await gate.AcquireAsync();
    ThrowIfStopping();
    return storage.Add(connection).Connection;
  }

  private async Task<T> RunRequestAsync<T>(Guid connectionId,
      Func<ProviderState, CancellationToken, Task<T>> operation, CancellationToken cancellationToken) {
    ProviderRequest request;
    long generation;
    using (await gate.AcquireAsync(cancellationToken)) {
      ThrowIfStopping();
      ProviderState state = storage.Get(connectionId);
      EnsureAvailable(state);
      generation = state.Generation;
      request = new(connectionId, cancellationToken);
      requests.Add(request);
    }
    try {
      ProviderState state = await GetCredentialsAsync(connectionId, generation, request.Cancellation.Token);
      request.Cancellation.Token.ThrowIfCancellationRequested();
      T result = await operation(state, request.Cancellation.Token);
      using var held = await gate.AcquireAsync(request.Cancellation.Token);
      request.Cancellation.Token.ThrowIfCancellationRequested();
      RequireCurrentGrant(state);
      return result;
    } finally {
      try {
        await request.Cancellation.CancelAsync();
      } finally {
        using var held = await gate.AcquireAsync(CancellationToken.None);
        requests.Remove(request);
        request.Cancellation.Dispose();
        request.Finished.TrySetResult();
      }
    }
  }

  private async Task<ProviderState> GetCredentialsAsync(Guid connectionId, long generation, CancellationToken token) {
    Task<ProviderState> refreshed;
    TaskCompletionSource<ProviderState>? completion = null;
    ProviderState state;
    using (await gate.AcquireAsync(token)) {
      ThrowIfStopping();
      state = storage.Get(connectionId);
      EnsureAvailable(state);
      if (state.Generation != generation)
        throw new ProviderOperationException(ProviderFailure.ConnectionChanged);
      if (state.Connection.Authentication == AuthenticationMethod.None)
        return state;
      ProviderCredentials credentials = state.Credentials
          ?? throw new ProviderOperationException(ProviderFailure.AuthenticationRequired);
      if (credentials.RequiresSignIn)
        throw new ProviderOperationException(ProviderFailure.AuthenticationRequired);
      if (state.Connection.Authentication == AuthenticationMethod.ApiKey)
        return state;
      if (!HasPlanAccess(credentials))
        throw new ProviderOperationException(ProviderFailure.PlanAccessDenied);
      if (credentials.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1))
        return state;
      if (state.Registration is null || string.IsNullOrEmpty(credentials.RefreshToken))
        throw new ProviderOperationException(ProviderFailure.AuthenticationRequired);
      if (!refreshes.TryGetValue(connectionId, out refreshed!)) {
        completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        refreshed = completion.Task;
        refreshes.Add(connectionId, refreshed);
      }
    }
    if (completion is not null)
      _ = RefreshAsync(state, completion);
    state = await refreshed.WaitAsync(token);
    using (await gate.AcquireAsync(token)) {
      EnsureAvailable(state);
      if (state.Credentials?.RequiresSignIn != false)
        throw new ProviderOperationException(ProviderFailure.AuthenticationRequired);
      if (!HasPlanAccess(state.Credentials))
        throw new ProviderOperationException(ProviderFailure.PlanAccessDenied);
      ProviderState current = storage.Get(connectionId);
      EnsureAvailable(current);
      if (current.Generation != generation)
        throw new ProviderOperationException(ProviderFailure.ConnectionChanged);
      return current;
    }
  }

  private async Task RefreshAsync(ProviderState state, TaskCompletionSource<ProviderState> completion) {
    Guid id = state.Connection.Id;
    try {
      using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
      ProviderCredentials credentials = await authentication.RefreshAsync(state.Registration!, state.Credentials!, timeout.Token);
      using var held = await gate.AcquireAsync();
      if (!storage.SaveRefresh(id, state.Generation, state.CredentialVersion, credentials))
        throw new ProviderOperationException(ProviderFailure.ConnectionChanged);
      completion.TrySetResult(storage.Get(id));
    } catch (Exception failure) {
      // A lost refresh acknowledgement may have rotated the remote token; require explicit recovery.
      using var held = await gate.AcquireAsync();
      if (failure is not ProviderOperationException { Failure: ProviderFailure.ProviderUnavailable or ProviderFailure.ConnectionChanged }) {
        fenced[id] = state.Generation;
        try {
          storage.SaveRefresh(id, state.Generation, state.CredentialVersion, state.Credentials! with { RequiresSignIn = true });
        } catch (Exception) {
          // If storage also failed, retain the local fence and report the original failure.
        }
      }
      completion.TrySetException(failure);
      _ = completion.Task.Exception;
    } finally {
      using var held = await gate.AcquireAsync();
      refreshes.Remove(id);
    }
  }

  private async Task DisconnectAsync(Guid id, Task joined, TaskCompletionSource<ProviderDisconnectResult> completion) {
    try {
      await joined;
      ProviderState state;
      using (await gate.AcquireAsync())
        state = storage.Get(id);
      bool? revoked = null;
      if (state.Registration is not null && !string.IsNullOrEmpty(state.Credentials?.RefreshToken)) {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try {
          revoked = await authentication.RevokeAsync(state.Registration, state.Credentials, timeout.Token);
        } catch (Exception) {
          revoked = false;
        }
      }
      using var held = await gate.AcquireAsync();
      storage.CompleteDisconnect(id, state.Generation);
      fenced.Remove(id);
      completion.TrySetResult(new(id, true, revoked));
    } catch (Exception failure) {
      completion.TrySetException(failure);
      _ = completion.Task.Exception;
    } finally {
      using var held = await gate.AcquireAsync();
      disconnects.Remove(id);
    }
  }

  // Caller holds the manager guard; callbacks and joins do not execute synchronously under it.
  private Task CancelRequests(Guid id) => Task.WhenAll(requests.Where(request => request.ConnectionId == id)
      .Select(request => Task.WhenAll(request.Cancellation.CancelAsync(), request.Finished.Task)));

  private void EnsureAvailable(ProviderState state) {
    if (state.Disconnecting)
      throw new ProviderOperationException(ProviderFailure.Disconnecting);
    if (IsFenced(state))
      throw new ProviderOperationException(ProviderFailure.AuthenticationRequired);
  }

  private void RequireCurrentGrant(ProviderState snapshot) {
    ProviderState current = storage.Get(snapshot.Connection.Id);
    EnsureAvailable(current);
    if (current.Generation != snapshot.Generation)
      throw new ProviderOperationException(ProviderFailure.ConnectionChanged);
    if (current.Connection.Authentication != AuthenticationMethod.OAuth)
      return;
    if (current.Credentials is null || current.Credentials.RequiresSignIn)
      throw new ProviderOperationException(ProviderFailure.AuthenticationRequired);
    if (!HasPlanAccess(current.Credentials))
      throw new ProviderOperationException(ProviderFailure.PlanAccessDenied);
    if (!current.Credentials.Scopes.ToHashSet(StringComparer.Ordinal).SetEquals(snapshot.Credentials!.Scopes))
      throw new ProviderOperationException(ProviderFailure.ConnectionChanged);
  }

  private ProviderCredentialStatus CredentialStatus(ProviderState state) => new(state.Connection.Id,
      state.Credentials is not null,
      state.Credentials?.RequiresSignIn == true || IsFenced(state) ||
          (state.Connection.Authentication == AuthenticationMethod.OAuth && state.Credentials is null),
      state.Registration?.Email, state.Credentials?.ExpiresAt,
      state.Connection.Authentication == AuthenticationMethod.OAuth
          ? state.Credentials is not null && HasPlanAccess(state.Credentials) : null,
      state.Disconnecting);

  private bool IsFenced(ProviderState state) => fenced.TryGetValue(state.Connection.Id, out long generation)
      && generation == state.Generation;

  private static bool HasPlanAccess(ProviderCredentials credentials) =>
      credentials.Scopes.Contains("resource.invoke", StringComparer.Ordinal) &&
      credentials.Scopes.Contains("chatgpt.tokens.use.direct", StringComparer.Ordinal);

  private static async Task ObserveCompletion(Task? task) {
    if (task is null)
      return;
    try {
      await task;
    } catch (Exception) {
      // The original operation reports its failure; shutdown still joins its completed work.
    }
  }

  private void ThrowIfStopping() {
    ObjectDisposedException.ThrowIf(disposed, this);
    if (stopping)
      throw new InvalidOperationException("Provider management is stopping.");
  }

  private static string ValidateName(string name) {
    if (string.IsNullOrWhiteSpace(name) || name.Length > 120 || name.Any(char.IsControl))
      throw new ArgumentException("A connection name must be nonempty and at most 120 characters.", nameof(name));
    return name.Trim();
  }

  private static CustomProviderSettings ValidateCustom(CustomProviderSettings settings) {
    ArgumentNullException.ThrowIfNull(settings);
    if (!Enum.IsDefined(settings.Api) || settings.Authentication is not (AuthenticationMethod.None or AuthenticationMethod.ApiKey))
      throw new ArgumentException("Select a supported API and either an API key or no authentication.", nameof(settings));
    Uri endpoint = settings.Endpoint;
    if (endpoint is null || !endpoint.IsAbsoluteUri || endpoint.Scheme is not ("http" or "https") ||
        endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0)
      throw new ArgumentException("An endpoint must be an HTTP(S) base URL without credentials, query or fragment.", nameof(settings));
    var normalized = new UriBuilder(endpoint) { Path = string.Concat(endpoint.AbsolutePath.TrimEnd('/'), '/') };
    return settings with { Name = ValidateName(settings.Name), Endpoint = normalized.Uri };
  }

  private static bool SameBinding(ProviderConnection left, ProviderConnection right) =>
      left.Id == right.Id && left.Route == right.Route && left.Api == right.Api &&
      left.Endpoint == right.Endpoint && left.Authentication == right.Authentication;
}
