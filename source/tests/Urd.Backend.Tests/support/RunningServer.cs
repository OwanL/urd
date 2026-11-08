using Microsoft.Extensions.AI;
using Urd.Core.Providers;
using Urd.Core.Storage;
using Urd.Server;
using static Urd.Backend.Tests.Support.TestAsync;

namespace Urd.Backend.Tests.Support;

// Owns a real Kestrel run, including startup failure and its shutdown barrier.
internal sealed class RunningServer : IAsyncDisposable {
  private readonly CancellationTokenSource stopping;
  private readonly TaskCompletionSource<Uri> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
  private readonly Task run;
  private bool completionObserved, disposed;

  internal RunningServer(ServerOptions options, Action<Uri>? onReady = null,
      Func<IChatClient>? createClient = null, Func<UrdStore, Func<Uri>, ProviderManager>? createProviders = null,
      CancellationToken cancellationToken = default) {
    stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    Client = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TestTimeout };
    run = Program.RunServerAsync(options, origin => {
      onReady?.Invoke(origin);
      ready.TrySetResult(origin);
    }, createClient ?? (static () => new FakeChatClient()), stopping.Token, createProviders);
  }

  internal HttpClient Client { get; }
  internal bool IsReady => ready.Task.IsCompletedSuccessfully;

  internal async Task<Uri> WaitForReadyAsync() {
    Task first = await Task.WhenAny(ready.Task, run).WaitAsync(TestTimeout, CancellationToken.None);
    if (first == run || run.IsCompleted) {
      await WaitForCompletionAsync();
      throw new InvalidOperationException("The server stopped before readiness could be used.");
    }

    Uri origin = await ready.Task;
    Client.BaseAddress = origin;
    return origin;
  }

  internal async Task WaitForCompletionAsync() {
    try {
      await run.WaitAsync(TestTimeout, CancellationToken.None);
    } finally {
      completionObserved = run.IsCompleted;
    }
  }

  internal void Cancel() => stopping.Cancel();

  public async ValueTask DisposeAsync() {
    if (disposed)
      return;

    await stopping.CancelAsync();
    try {
      await run.WaitAsync(TestTimeout, CancellationToken.None);
    } catch when (completionObserved && run.IsCompleted) {
      // A failure explicitly awaited by the test need not be thrown a second time during cleanup.
    } finally {
      // A timeout is not a join: keep borrowed resources alive underneath unfinished work.
      if (run.IsCompleted) {
        Client.Dispose();
        stopping.Dispose();
        disposed = true;
      }
    }
  }
}
