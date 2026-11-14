using Microsoft.Extensions.AI;

namespace Urd.Core.Usage;

// Preserve EOF separately from disposal: callers may flush output before joining provider cleanup.
internal sealed class UsageStream(
    Func<CancellationToken, (IAsyncEnumerator<ChatResponseUpdate> Updates, UsageObservation Observation)> start)
    : IAsyncEnumerable<ChatResponseUpdate> {
  public IAsyncEnumerator<ChatResponseUpdate> GetAsyncEnumerator(CancellationToken cancellationToken = default) {
    var (updates, observation) = start(cancellationToken);
    return new Enumerator(updates, observation);
  }

  private sealed class Enumerator(IAsyncEnumerator<ChatResponseUpdate> updates, UsageObservation observation)
      : IAsyncEnumerator<ChatResponseUpdate> {
    public ChatResponseUpdate Current => updates.Current;

    public async ValueTask<bool> MoveNextAsync() {
      bool next = await updates.MoveNextAsync();
      if (next)
        observation.Append(updates.Current);
      else
        observation.Finish();
      return next;
    }

    public async ValueTask DisposeAsync() {
      try {
        await updates.DisposeAsync();
      } finally {
        // Caller cancellation cannot discard consumption already observed before failure or Stop.
        observation.Finish();
      }
    }
  }
}
