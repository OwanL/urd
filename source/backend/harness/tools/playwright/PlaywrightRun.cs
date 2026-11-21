using Microsoft.Playwright;
using Urd.Core.Sessions;

namespace Urd.Core.Tools;

internal sealed class PlaywrightRun(SessionManager sessions, PlaywrightProfileLease lease, PlaywrightNetwork network) : IAsyncDisposable {
  private IPage? page;
  private bool signInAbandoned;

  internal Uri? Entry { get; set; }
  internal bool AllowsNavigation(Uri url) => network.AllowsNavigation(url);

  internal Task<IPage> PageAsync(CancellationToken cancellation) => lease.WithAutomationAsync(async (context, token) => {
    if (signInAbandoned)
      throw new InvalidOperationException("Sign-in was cancelled for this task.");
    if (page is not null && !page.IsClosed)
      return page;
    page = context.Pages.FirstOrDefault(tab => !tab.IsClosed) ?? await context.NewPageAsync().WaitAsync(token);
    page.SetDefaultTimeout(10_000);
    page.SetDefaultNavigationTimeout(15_000);
    return page;
  }, cancellation);

  internal async Task<bool> SignInAsync(Guid sessionId, Uri entry, CancellationToken cancellation) {
    if (signInAbandoned || !AllowsNavigation(entry))
      return false;
    // Restart from the tool's entry point and let the application create a new auth transaction.
    // A live OAuth redirect can depend on in-memory state from the old tab.
    string origin = entry.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped);
    bool continued = await sessions.RunBrowserSignInAsync(sessionId, origin, async (continueSignal, markVisible, token) => {
      page = null;
      return await lease.HandoffAsync(continueSignal, async (context, setup) => {
        IPage visible = context.Pages.FirstOrDefault(tab => !tab.IsClosed) ?? await context.NewPageAsync().WaitAsync(setup);
        await visible.GotoAsync(entry.AbsoluteUri, new() { Timeout = 15_000, WaitUntil = WaitUntilState.Commit }).WaitAsync(setup);
        await markVisible();
      }, token);
    }, cancellation);
    signInAbandoned = !continued;
    return continued;
  }

  public async ValueTask DisposeAsync() {
    try {
      await lease.DisposeAsync();
    } finally {
      await network.DisposeAsync();
    }
  }
}
