using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Playwright;

namespace Urd.Core.Tools;

// Per-session state, weakly keyed by live page. A full-page capture from a sequential
// foreground action can serve as the baseline, even if the shared output cap spills the result.
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
      bool full, string? @ref, CancellationToken cancellation) {
    if (@ref is not null) {
      string subtree = await page.GetByRef(@ref).AriaSnapshotAsync(new() { Mode = AriaSnapshotMode.Ai, Timeout = 2000 })
          .WaitAsync(cancellation).ConfigureAwait(false);
      return $"{header}\n{subtree}";
    }

    string url = page.Url;
    int document = interaction.Document;
    string snapshot = await page.AriaSnapshotAsync(new() { Mode = AriaSnapshotMode.Ai })
        .WaitAsync(cancellation).ConfigureAwait(false);
    lock (gate) {
      bool stable = !independent && document == interaction.Document && url == page.Url;
      if (!stable)
        return $"{header}\n{snapshot}";

      string result = !full && pages.TryGetValue(page, out Baseline? before) && before.Url == url
          && before.Document == document
          ? ChooseView(header, snapshot, SnapshotTree.Changes(before.Text, snapshot))
          : $"{header}\n{snapshot}";
      pages.AddOrUpdate(page, new Baseline(url, document, snapshot));
      return result;
    }
  }

  private static string ChooseView(string header, string snapshot, string? changes) {
    if (changes is null)
      return $"{header}\nPage unchanged.";
    if (ReferenceEquals(changes, snapshot))
      return $"{header}\n{snapshot}";

    string diff = $"{header}\n{changes}";
    bool compact = Encoding.UTF8.GetByteCount(changes) < Encoding.UTF8.GetByteCount(snapshot) / 2;
    bool diffFits = ToolOutputLimit.Fits(diff);
    if (compact && diffFits)
      return diff;

    // Prefer a complete inline view to spilling. Otherwise prefer the full tree unless the
    // diff is substantially smaller, even when both views are too large to return inline.
    string full = $"{header}\n{snapshot}";
    return (compact || diffFits) && !ToolOutputLimit.Fits(full) ? diff : full;
  }
}
