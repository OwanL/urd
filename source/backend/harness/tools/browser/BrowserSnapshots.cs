using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Playwright;

namespace Urd.Core.Tools;

// Per-session state, weakly keyed by live page. Only an inline result from a sequential
// foreground action can serve as the baseline for a later model request.
internal sealed class BrowserSnapshots {
  private sealed record Baseline(string Url, int Document, string Text);

  private readonly Lock gate = new();
  private readonly ConditionalWeakTable<IPage, Baseline> pages = new();
  private Guid execution;
  private Guid request;
  private int active;
  private bool independent;

  internal void Begin(HarnessToolContext context) {
    lock (gate) {
      // Sibling results can be delivered out of capture order. Start fresh after that batch,
      // after deferred results, or when a new execution may no longer include the old history.
      independent = active > 0 || context.Background || request == context.RequestId;
      if (independent || execution != context.ExecutionId)
        pages.Clear();
      execution = context.ExecutionId;
      request = context.RequestId;
      active++;
    }
  }

  internal void End() {
    lock (gate)
      active--;
  }

  internal async Task<string> DescribeAsync(IPage page, BrowserInteraction interaction, string header,
      bool full, CancellationToken cancellation) {
    string url = page.Url;
    int document = interaction.Document;
    string snapshot = await page.AriaSnapshotAsync(new() { Mode = AriaSnapshotMode.Ai })
        .WaitAsync(cancellation).ConfigureAwait(false);
    lock (gate) {
      string view = snapshot;
      bool stable = !independent && document == interaction.Document && url == page.Url;
      if (stable && !full && pages.TryGetValue(page, out Baseline? before) && before.Url == url
          && before.Document == document) {
        string? changes = SnapshotTree.Changes(before.Text, snapshot);
        if (changes is null)
          view = "Page unchanged.";
        else if (Encoding.UTF8.GetByteCount(changes) < Encoding.UTF8.GetByteCount(snapshot) / 2)
          view = changes;
      }
      string result = $"{header}\n{view}";
      if (!ToolOutputLimit.Fits(result)) {
        // A partial update is not a usable description, even if the page later reverts.
        pages.Remove(page);
        return $"Page view truncated; the next snapshot will be full.\n{result}";
      }
      if (stable)
        pages.AddOrUpdate(page, new Baseline(url, document, snapshot));
      return result;
    }
  }
}
