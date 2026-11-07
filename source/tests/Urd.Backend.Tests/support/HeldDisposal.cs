using Microsoft.Extensions.AI;

namespace Urd.Backend.Tests.Support;

// Holds LLM cleanup after the source has been disposed, before request settlement.
internal sealed class HeldDisposal(IAsyncEnumerable<ChatResponseUpdate> source,
    TaskCompletionSource entered, Task release) : IAsyncEnumerable<ChatResponseUpdate> {
  public IAsyncEnumerator<ChatResponseUpdate> GetAsyncEnumerator(CancellationToken cancellationToken = default)
      => new Enumerator(source.GetAsyncEnumerator(cancellationToken), entered, release);

  private sealed class Enumerator(IAsyncEnumerator<ChatResponseUpdate> inner,
      TaskCompletionSource entered, Task release) : IAsyncEnumerator<ChatResponseUpdate> {
    public ChatResponseUpdate Current => inner.Current;
    public ValueTask<bool> MoveNextAsync() => inner.MoveNextAsync();
    public async ValueTask DisposeAsync() {
      await inner.DisposeAsync();
      entered.TrySetResult();
      await release;
    }
  }
}
