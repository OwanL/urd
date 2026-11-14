using Microsoft.Extensions.AI;
using Urd.Core.Providers;
using Urd.Core.Storage;

namespace Urd.Core.Usage;

// A borrowed view: usage belongs to each invocation, not to this client or its disposal.
internal sealed class UsageChatClient(IChatClient inner, UsageStorage storage, LlmSelection selection,
    Guid? sessionId, Guid? executionId, string purpose) : IChatClient {
  public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
      CancellationToken cancellationToken = default) {
    cancellationToken.ThrowIfCancellationRequested();
    ChatMessage[] history = messages.ToArray();
    options ??= new() { ModelId = selection.ModelId };
    var observation = new UsageObservation(storage, selection, sessionId, executionId, purpose, history, options);
    try {
      if (inner is ProviderChatClient provider)
        return await provider.GetObservedResponseAsync(history, options, observation.Append, cancellationToken);
      ChatResponse response = await inner.GetResponseAsync(history, options, cancellationToken);
      foreach (ChatMessage message in response.Messages)
        observation.Append(new() { ModelId = response.ModelId, Contents = message.Contents });
      if (response.Usage is { } usage)
        observation.Append(new() { ModelId = response.ModelId, Contents = [new UsageContent(usage)] });
      return response;
    } finally {
      observation.Finish();
    }
  }

  public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
      ChatOptions? options = null, CancellationToken cancellationToken = default) => new UsageStream(token => {
    cancellationToken.ThrowIfCancellationRequested();
    token.ThrowIfCancellationRequested();
    ChatMessage[] history = messages.ToArray();
    ChatOptions requestOptions = options ?? new() { ModelId = selection.ModelId };
    var observation = new UsageObservation(storage, selection, sessionId, executionId, purpose, history, requestOptions);
    try {
      return (inner.GetStreamingResponseAsync(history, requestOptions, cancellationToken).GetAsyncEnumerator(token), observation);
    } catch {
      observation.Finish();
      throw;
    }
  });

  public object? GetService(Type serviceType, object? serviceKey = null) {
    ArgumentNullException.ThrowIfNull(serviceType);
    return serviceKey is null && serviceType.IsInstanceOfType(this) ? this : inner.GetService(serviceType, serviceKey);
  }

  public void Dispose() { } // The session/provider manager owns the underlying client.
}
