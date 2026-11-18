using Microsoft.Playwright;

namespace Urd.Core.Tools;

internal sealed class WebBrowser : IAsyncDisposable {
  private const int MaximumParticipants = 10;
  private readonly object sync = new();
  private readonly SemaphoreSlim slots = new(2);
  private readonly CancellationTokenSource lifetime = new();
  private readonly TaskCompletionSource drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
  private readonly string cache;
  private readonly TimeSpan idleTimeout;
  private readonly IProgress<string>? progress;
  private Task<WebBrowserInstance>? running;
  private CancellationTokenSource? launch;
  private CancellationTokenSource? idle;
  private Task retiring = Task.CompletedTask;
  private Task? disposing;
  private int participants;
  private int active;

  public WebBrowser(string? cache = null, TimeSpan? idleTimeout = null, IProgress<string>? progress = null) {
    this.cache = Path.GetFullPath(cache ?? WebBrowserInstall.DefaultCache);
    this.idleTimeout = idleTimeout ?? TimeSpan.FromMinutes(1);
    this.progress = progress;
  }

  public async Task<T> WithPageAsync<T>(Func<IPage, CancellationToken, Task<T>> operation, CancellationToken cancellation) {
    ArgumentNullException.ThrowIfNull(operation);
    cancellation.ThrowIfCancellationRequested();
    Enter();
    try {
      using var use = CancellationTokenSource.CreateLinkedTokenSource(cancellation, lifetime.Token);
      if (!await slots.WaitAsync(TimeSpan.FromSeconds(10), use.Token))
        throw new TimeoutException("Timed out waiting for a browser slot.");
      try {
        WebBrowserInstance instance = await BeginUse().WaitAsync(use.Token);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(use.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        try {
          return await VisitAsync(instance, operation, deadline.Token);
        } catch (OperationCanceledException) when (!use.IsCancellationRequested && deadline.IsCancellationRequested) {
          throw new TimeoutException("The browser operation timed out.");
        }
      } finally {
        EndUse();
        slots.Release();
      }
    } finally {
      lock (sync) {
        if (--participants == 0 && disposing is not null)
          drained.TrySetResult();
      }
    }
  }

  public ValueTask DisposeAsync() {
    lock (sync) {
      if (disposing is null) {
        disposing = DisposeCoreAsync();
        if (participants == 0)
          drained.TrySetResult();
      }
      return new ValueTask(disposing);
    }
  }

  private async Task DisposeCoreAsync() {
    // DisposeAsync publishes the shutdown task before any caller cancellation or browser cleanup.
    await Task.Yield();
    await lifetime.CancelAsync();
    await drained.Task;
    Task cleanup;
    lock (sync) {
      CancelIdle();
      Retire();
      cleanup = retiring;
    }
    try {
      await cleanup;
    } finally {
      lifetime.Dispose();
      slots.Dispose();
    }
  }

  private void Enter() {
    lock (sync) {
      ObjectDisposedException.ThrowIf(disposing is not null, this);
      if (participants == MaximumParticipants)
        throw new InvalidOperationException("The browser queue is full.");
      participants++;
    }
  }

  private Task<WebBrowserInstance> BeginUse() {
    lock (sync) {
      active++;
      CancelIdle();
      if (running is { IsCompleted: true } && (!running.IsCompletedSuccessfully || !running.Result.IsReusable)) {
        if (active > 1)
          throw new InvalidOperationException("The browser is restarting; retry after the current operation finishes.");
        Retire();
      }
      if (running is null) {
#pragma warning disable S2930 // Retirement owns cancellation and disposal of the launch source.
        launch = new CancellationTokenSource(TimeSpan.FromMinutes(5));
#pragma warning restore S2930
        running = StartAsync(retiring, launch.Token);
      }
      return running;
    }
  }

  private void EndUse() {
    lock (sync) {
      if (--active != 0 || running is null || disposing is not null)
        return;
      if (!running.IsCompletedSuccessfully || !running.Result.IsReusable) {
        Retire();
        return;
      }
      idle = new CancellationTokenSource();
      _ = WaitForIdleAsync(idle);
    }
  }

  private async Task WaitForIdleAsync(CancellationTokenSource countdown) {
    try {
      await Task.Delay(idleTimeout, countdown.Token);
      lock (sync) {
        if (idle != countdown)
          return;
        idle = null;
        Retire();
      }
    } catch (OperationCanceledException) {
      // A caller renewed the idle lease.
    } finally {
      countdown.Dispose();
    }
  }

  private void CancelIdle() {
    CancellationTokenSource? countdown = idle;
    idle = null;
    countdown?.Cancel();
  }

  private async Task<WebBrowserInstance> StartAsync(Task predecessor, CancellationToken cancellation) {
    await Task.Yield();
    await predecessor;
    try {
      return await WebBrowserInstance.StartAsync(cache, progress, cancellation);
    } catch (OperationCanceledException) when (cancellation.IsCancellationRequested) {
      progress?.Report(WebBrowserInstall.SetupFailed);
      throw new TimeoutException("Chromium setup did not finish in time.");
    } catch (Exception) {
      // Setup callers report the failure; progress ends any surfaced download notice.
      progress?.Report(WebBrowserInstall.SetupFailed);
      throw;
    }
  }

  private void Retire() {
    if (running is null)
      return;
    retiring = StopAsync(running, launch!);
    running = null;
    launch = null;
  }

  private static async Task StopAsync(Task<WebBrowserInstance> setup, CancellationTokenSource cancellation) {
    await cancellation.CancelAsync();
    try {
      WebBrowserInstance instance;
      try {
        instance = await setup;
      } catch (Exception) {
        // Setup already reports its failure to waiting callers and cleans its own resources.
        return;
      }
      await instance.DisposeAsync();
    } finally {
      cancellation.Dispose();
    }
  }

  private static async Task<T> VisitAsync<T>(WebBrowserInstance instance, Func<IPage, CancellationToken, Task<T>> operation,
      CancellationToken cancellation) {
    cancellation.ThrowIfCancellationRequested();
    await using var proxy = new WebBrowserProxy(instance.Certificate);
    Task<IBrowserContext> opening = instance.Browser.NewContextAsync(new BrowserNewContextOptions {
      Proxy = new Proxy { Server = proxy.Address, Bypass = "<-loopback>" },
      ServiceWorkers = ServiceWorkerPolicy.Block,
      AcceptDownloads = true
    });
    Task<T>? work = null;
    try {
      IBrowserContext context = await opening.WaitAsync(cancellation);
      await context.RouteAsync(url => Uri.TryCreate(url, UriKind.Absolute, out Uri? address) && address.IsFile,
          route => route.AbortAsync()).WaitAsync(cancellation);
      IPage page = await context.NewPageAsync().WaitAsync(cancellation);
      work = operation(page, cancellation);
      return await work.WaitAsync(cancellation);
    } finally {
      await CloseContextAsync(instance, opening);
      if (work is not null && cancellation.IsCancellationRequested)
        ObserveFailure(work);
    }
  }

  private static async Task CloseContextAsync(WebBrowserInstance instance, Task<IBrowserContext> opening) {
    static async Task CloseAsync(Task<IBrowserContext> opening) => await (await opening).CloseAsync();

    Task closing = CloseAsync(opening);
    try {
      // If the driver hangs, retire it after its other operation is done, not during it.
      await closing.WaitAsync(TimeSpan.FromSeconds(4));
    } catch (PlaywrightException) {
      // The renderer or browser may have already exited.
    } catch (TimeoutException) {
      instance.MarkUnusable();
      ObserveFailure(closing);
    }
  }

  private static void ObserveFailure(Task pending) => _ = pending.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
      TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
}
