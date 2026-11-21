namespace Urd.Core.Sessions;

public enum BrowserSignInStage { Requested, Opening, Visible }

public sealed record BrowserSignInView(Guid Id, string Origin, BrowserSignInStage Stage);

public sealed partial class PrimarySession {
  private BrowserSignInPrompt? browserSignIn;

  internal Task<BrowserSignInView?> ReadBrowserSignInAsync(CancellationToken cancellation) =>
      ReadUnderLockAsync(() => browserSignIn?.View, cancellation);

  internal async Task<bool> ActOnBrowserSignInAsync(Guid id, string action, CancellationToken cancellation) {
    return await ReadUnderLockAsync(() => {
      BrowserSignInPrompt? prompt = browserSignIn;
      if (prompt is null || prompt.View.Id != id)
        return false;
      switch (action) {
        case "open" when prompt.View.Stage == BrowserSignInStage.Requested:
          prompt.View = prompt.View with { Stage = BrowserSignInStage.Opening };
          PublishChange();
          prompt.Open.TrySetResult();
          return true;
        case "continue" when prompt.View.Stage == BrowserSignInStage.Visible && prompt.Window?.IsCompleted != true:
          browserSignIn = null;
          PublishChange();
          prompt.Continue.TrySetResult();
          return true;
        case "cancel":
          browserSignIn = null;
          PublishChange();
          prompt.Cancel.TrySetResult();
          return true;
        default:
          return false;
      }
    }, cancellation);
  }

  internal async Task<bool> RunBrowserSignInAsync(string origin,
      Func<Task, Func<Task>, CancellationToken, Task<bool>> openWindow, CancellationToken cancellation) {
    ArgumentNullException.ThrowIfNull(origin);
    ArgumentNullException.ThrowIfNull(openWindow);
    var prompt = new BrowserSignInPrompt(new(Guid.NewGuid(), origin, BrowserSignInStage.Requested));
    using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
    await ReadUnderLockAsync(() => {
      cancellation.ThrowIfCancellationRequested();
      ThrowIfClosed();
      if (browserSignIn is not null)
        throw new InvalidOperationException("A browser sign-in is already pending for this session.");
      browserSignIn = prompt;
      PublishChange();
      return true;
    }, cancellation);
    try {
      Task first = await Task.WhenAny(prompt.Open.Task, prompt.Cancel.Task, Task.Delay(Timeout.Infinite, linked.Token));
      cancellation.ThrowIfCancellationRequested();
      if (first != prompt.Open.Task || prompt.Cancel.Task.IsCompleted)
        return false;

      Task MarkVisibleAsync() => ReadUnderLockAsync(() => {
        if (ReferenceEquals(browserSignIn, prompt) && !linked.IsCancellationRequested
            && prompt.View.Stage == BrowserSignInStage.Opening) {
          prompt.View = prompt.View with { Stage = BrowserSignInStage.Visible };
          PublishChange();
        }
        return true;
      }, CancellationToken.None);

      Task<bool> window = openWindow(prompt.Continue.Task, MarkVisibleAsync, linked.Token);
      prompt.Window = window;
      Task settled = await Task.WhenAny(window, prompt.Cancel.Task, Task.Delay(Timeout.Infinite, linked.Token));
      if (settled != window) {
        await ReadUnderLockAsync(() => RemoveBrowserSignIn(prompt), CancellationToken.None);
        await linked.CancelAsync();
        try {
          await window;
        } catch (OperationCanceledException) when (linked.IsCancellationRequested) {
          // Cancellation is a normal outcome for a cancelled prompt.
        }
        cancellation.ThrowIfCancellationRequested();
        return false;
      }
      bool result = await window;
      cancellation.ThrowIfCancellationRequested();
      // Serialize closure against a racing continue; a closed window cannot continue later.
      return await ReadUnderLockAsync(() => {
        bool continued = prompt.Continue.Task.IsCompleted;
        RemoveBrowserSignIn(prompt);
        return result && continued && !cancellation.IsCancellationRequested;
      }, CancellationToken.None);
    } finally {
      await ReadUnderLockAsync(() => RemoveBrowserSignIn(prompt), CancellationToken.None);
    }
  }

  private bool RemoveBrowserSignIn(BrowserSignInPrompt prompt) {
    if (!ReferenceEquals(browserSignIn, prompt))
      return false;
    browserSignIn = null;
    PublishChange();
    return true;
  }

  private sealed class BrowserSignInPrompt(BrowserSignInView view) {
    internal BrowserSignInView View { get; set; } = view;
    internal volatile Task<bool>? Window;
    internal TaskCompletionSource Open { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource Cancel { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
  }
}
