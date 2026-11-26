namespace Urd.Core.Tools;

// Runs a page command while watching for dialogs and the navigation it may start.
internal sealed partial class BrowserTool {
  private async Task<ToolOutput> RunActionAsync(Guid owner, BrowserInteraction interaction, string note,
      Func<Task> command, CancellationToken cancellation, bool allowGrace = false) {
    List<string> notes = [note];
    PageCommandResult result = await RunPagedCommandAsync(interaction, command, cancellation, notes, allowGrace)
        .ConfigureAwait(false);
    if (result == PageCommandResult.Dialog)
      notes.Clear();
    // A snapshot can wait for the new document's body even after commit.
    return await ResultAsync(owner, notes, [], withSnapshot: result != PageCommandResult.NavigationTimedOut,
        cancellation).ConfigureAwait(false);
  }

  private enum PageCommandResult { Ready, Dialog, NavigationTimedOut }

  // The dialog signal covers both the command and any navigation it started. Stop waiting on
  // cancellation or a dialog without retrying input that may already have taken effect.
  private static async Task<PageCommandResult> RunPagedCommandAsync(BrowserInteraction interaction, Func<Task> command,
      CancellationToken cancellation, List<string>? actionNotes = null, bool allowGrace = false) {
    cancellation.ThrowIfCancellationRequested();
    using CancellationTokenSource waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
    Task dialogPending = interaction.WaitDialogPendingAsync(waiting.Token);
    int before = interaction.Navigation.Version;
    try {
      if (dialogPending.IsCompleted) {
        cancellation.ThrowIfCancellationRequested();
        return PageCommandResult.Dialog;
      }
      Task commandTask = command();
      if (await Task.WhenAny(commandTask, dialogPending).ConfigureAwait(false) != commandTask) {
        _ = AwaitQuietlyAsync(commandTask);
        cancellation.ThrowIfCancellationRequested();
        return PageCommandResult.Dialog;
      }
      if (cancellation.IsCancellationRequested || interaction.PendingDialog is not null) {
        _ = AwaitQuietlyAsync(commandTask);
        cancellation.ThrowIfCancellationRequested();
        return PageCommandResult.Dialog;
      }
      await commandTask.ConfigureAwait(false);
      NavigationWait navigation = NavigationWait.Ready;
      if (actionNotes is not null) {
        Task<NavigationWait> loading = interaction.Navigation.WaitAfterAsync(before, allowGrace, waiting.Token);
        if (await Task.WhenAny(loading, dialogPending).ConfigureAwait(false) == loading)
          navigation = await loading.ConfigureAwait(false);
      }
      cancellation.ThrowIfCancellationRequested();
      if (dialogPending.IsCompleted)
        return PageCommandResult.Dialog;
      if (navigation != NavigationWait.Ready) {
        string milestone = navigation == NavigationWait.TimedOutBeforeCommit ? "commit" : "reach DOMContentLoaded";
        actionNotes!.Add($"Navigation did not {milestone} within 10 seconds. Page view omitted; use snapshot after the page loads.");
        return PageCommandResult.NavigationTimedOut;
      }
      return PageCommandResult.Ready;
    } finally {
      await waiting.CancelAsync().ConfigureAwait(false);
    }
  }

  private static async Task AwaitQuietlyAsync(Task command) {
    try {
      await command.ConfigureAwait(false);
    } catch {
      // The abandoned command fails once the dialog is answered or the page is gone.
    }
  }
}
