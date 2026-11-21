using Microsoft.Playwright;

namespace Urd.Core.Tools;

internal sealed class PlaywrightProfileBrowser : IPlaywrightProfileBrowser {
  private readonly IPlaywright driver;
  private readonly TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

  public PlaywrightProfileBrowser(IPlaywright driver, IBrowserContext context) {
    this.driver = driver;
    Context = context;
    context.Close += (_, _) => closed.TrySetResult();
  }

  public IBrowserContext Context { get; }
  public Task Closed => closed.Task;

  public async Task CloseAsync() {
    try {
      await Context.CloseAsync().WaitAsync(TimeSpan.FromSeconds(5));
    } finally {
      driver.Dispose();
    }
  }
}
