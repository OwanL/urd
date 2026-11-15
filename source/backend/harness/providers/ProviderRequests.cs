using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Urd.Core.Concurrency;
using Urd.Core.Storage;

namespace Urd.Core.Providers;

// Tracks provider requests and drains cancellation before releasing borrowed transport and storage.
internal sealed class ProviderRequests(
    AsyncLock gate, ProviderStorage storage, OpenAIInferenceClient inference,
    Func<Guid, long, CancellationToken, Task<ProviderState>> getCredentials,
    Action throwIfStopping, Action<ProviderState> ensureAvailable, Action<ProviderState> requireCurrentGrant) {
  private readonly List<ProviderRequest> requests = [];

  internal async Task<T> RunRequestAsync<T>(Guid connectionId,
      Func<ProviderState, CancellationToken, Task<T>> operation, CancellationToken cancellationToken) {
    var (request, generation) = await BeginRequestAsync(connectionId, cancellationToken);
    try {
      ProviderState state = await getCredentials(connectionId, generation, request.Cancellation.Token);
      request.Cancellation.Token.ThrowIfCancellationRequested();
      T result = await operation(state, request.Cancellation.Token);
      using var held = await gate.AcquireAsync(request.Cancellation.Token);
      request.Cancellation.Token.ThrowIfCancellationRequested();
      requireCurrentGrant(state);
      return result;
    } finally {
      await FinishRequestAsync(request, cancellationToken: CancellationToken.None);
    }
  }

  internal async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
      ProviderConnection expectedBinding, IEnumerable<ChatMessage> messages, ChatOptions options,
      [EnumeratorCancellation] CancellationToken cancellationToken) {
    var (request, generation) = await BeginRequestAsync(expectedBinding.Id, cancellationToken,
        Timeout.InfiniteTimeSpan, expectedBinding);
    ChatResponseUpdate? completion = null;
    ProviderState? completedState = null;
    try {
      ProviderState state = await getCredentials(expectedBinding.Id, generation, request.Cancellation.Token);
      request.Cancellation.Token.ThrowIfCancellationRequested();
      await foreach (var update in inference.GetStreamingResponseAsync(state, messages, options, request.Cancellation.Token)) {
        if (completion is not null)
          throw new ProviderOperationException(ProviderFailure.InvalidProviderResponse);
        if (update.FinishReason is not null)
          completion = update;
        else
          yield return update;
      }
      request.Cancellation.Token.ThrowIfCancellationRequested();
      if (completion is null)
        throw new ProviderOperationException(ProviderFailure.IncompleteResponse);
      completedState = state;
    } finally {
      await FinishRequestAsync(request, completedState, cancellationToken);
    }
    // No executable calls or native-context completion escape before the manager accepts the result.
    yield return completion;
  }

  private async Task<(ProviderRequest Request, long Generation)> BeginRequestAsync(Guid connectionId,
      CancellationToken cancellationToken, TimeSpan? timeout = null, ProviderConnection? expectedBinding = null) {
    using var held = await gate.AcquireAsync(cancellationToken);
    throwIfStopping();
    ProviderState state = storage.Get(connectionId);
    ensureAvailable(state);
    if (expectedBinding is not null && !ProviderManager.SameBinding(state.Connection, expectedBinding))
      throw new ProviderOperationException(ProviderFailure.ConnectionChanged);
    var request = new ProviderRequest(connectionId, cancellationToken, timeout);
    requests.Add(request);
    return (request, state.Generation);
  }

  private async Task FinishRequestAsync(ProviderRequest request, ProviderState? completedState = null,
      CancellationToken cancellationToken = default) {
    bool drained = false;
    try {
      await request.Cancellation.CancelAsync();
      drained = true;
    } finally {
      using var held = await gate.AcquireAsync(CancellationToken.None);
      try {
        if (drained && completedState is not null) {
          cancellationToken.ThrowIfCancellationRequested();
          throwIfStopping();
          requireCurrentGrant(completedState);
        }
      } finally {
        requests.Remove(request);
        request.Cancellation.Dispose();
        request.Finished.TrySetResult();
      }
    }
  }

  // Caller holds the shared guard; callbacks and joins do not execute synchronously under it.
  internal Task Cancel(Guid? connectionId = null) => Task.WhenAll(requests
      .Where(request => connectionId is null || request.ConnectionId == connectionId)
      .Select(request => Task.WhenAll(request.Cancellation.CancelAsync(), request.Finished.Task)));
}
