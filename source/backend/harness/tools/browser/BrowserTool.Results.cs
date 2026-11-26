using Microsoft.Extensions.AI;
using Microsoft.Playwright;

namespace Urd.Core.Tools;

// Builds the tool result: notes, owner notices, page events and the page view or blocking dialog.
internal sealed partial class BrowserTool {
  private Task<ToolOutput> ResultAsync(Guid owner, List<string> notes, CancellationToken cancellation,
      bool full = false, string? @ref = null) =>
      ResultAsync(owner, notes, [], withSnapshot: true, cancellation, full, @ref);

  private async Task<ToolOutput> ResultAsync(Guid owner, List<string> notes, IReadOnlyList<DataContent> images,
      bool withSnapshot, CancellationToken cancellation, bool full = false, string? @ref = null) {
    BrowserPage<IPage>? active = host.Pages.Selected(owner);
    List<string> lines = [.. notes];
    if (active is null) {
      lines.Add("No pages are open.");
      lines.AddRange(Notices(owner));
      return new ToolOutput(Body(lines), images);
    }
    BrowserInteraction interaction = BrowserInteraction.For(active.Page);
    host.Touch(owner, active.Id);
    lines.AddRange(Notices(owner));
    if (interaction.PendingDialog is { } open) {
      lines.Add(DialogBlock(open));
    } else {
      (string[] errors, string[] failed) = interaction.DrainEvents();
      try {
        lines.AddRange(errors.Select(text => $"Console error: {text}"));
        lines.AddRange(failed.Select(text => $"Failed request: {text}"));
        if (withSnapshot) {
          lines.Add(active.Page.Url);
          try {
            string text = await snapshots.DescribeAsync(active.Page, interaction, Body(lines), full, @ref,
                cancellation).ConfigureAwait(false);
            return new ToolOutput(text, images);
          } catch (Exception error) when (error is PlaywrightException or TimeoutException) {
            // The page may have opened a dialog while the action ran; that stays the headline.
            if (interaction.PendingDialog is not { } appeared)
              throw;
            lines.Add(DialogBlock(appeared));
          }
        }
        return new ToolOutput(Body(lines), images);
      } catch {
        // A result was not built, so deduplicated events must be available to the next action.
        interaction.RestoreEvents(errors, failed);
        throw;
      }
    }
    return new ToolOutput(Body(lines), images);
  }

  private string[] Notices(Guid owner) {
    string drained = host.DrainNotices(owner);
    return drained.Length == 0 ? [] : drained.Split(Environment.NewLine);
  }

  private static string DialogBlock(PendingDialog open) {
    string detail = string.IsNullOrEmpty(open.DefaultValue) ? "" : $" (default \"{open.DefaultValue}\")";
    return $"A dialog blocks this page: {open.Type} \"{open.Message}\"{detail}. "
        + "Resolve it with the dialog action; other actions on this page wait until then.";
  }

  private static string Body(IEnumerable<string> lines) => string.Join('\n', lines.Where(line => line.Length > 0));
}
