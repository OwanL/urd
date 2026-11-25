using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Playwright;

namespace Urd.Core.Tools;

// A full-page capture from a sequential foreground action can serve as the baseline,
// even if the shared output cap spills the result.
internal sealed class BrowserSnapshots {
  private sealed record Baseline(string Url, int Document, string Text, string CallId);
  private sealed record Capture(long Generation, string CallId, bool Independent);

  private readonly Lock gate = new();
  private readonly ConditionalWeakTable<IPage, Baseline> pages = new();
  private readonly AsyncLocal<Capture?> capture = new();
  private Guid session;
  private Guid request;
  private object? stamp;
  private long generation;
  private int active;

  internal void Begin(HarnessToolContext context) {
    lock (gate) {
      object? nextStamp = context.BrowserContextStamp;
      // Sibling and background results can arrive out of order. Fence every capture already
      // underway as well as the new one, and start fresh after the batch.
      bool independent = active > 0 || context.Background || request == context.RequestId || nextStamp is null;
      if (independent || session != context.SessionId || !Equals(stamp, nextStamp)) {
        pages.Clear();
        generation++;
      }
      session = context.SessionId;
      stamp = nextStamp;
      request = context.RequestId;
      active++;
      capture.Value = new Capture(generation, context.CallId, independent);
    }
  }

  internal void End() {
    lock (gate)
      active--;
    capture.Value = null;
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
      Capture? current = capture.Value;
      bool stable = current is not null && !current.Independent && current.Generation == generation
          && document == interaction.Document && url == page.Url;
      if (!stable)
        return $"{header}\n{snapshot}";

      string result = !full && pages.TryGetValue(page, out Baseline? before) && before.Url == url
          && before.Document == document
          ? ChooseView(header, snapshot, SnapshotTree.Changes(before.Text, snapshot), before.CallId)
          : $"{header}\n{snapshot}";
      pages.AddOrUpdate(page, new Baseline(url, document, snapshot, current!.CallId));
      return result;
    }
  }

  private static string ChooseView(string header, string snapshot, string? changes, string callId) {
    if (changes is null)
      return $"{header}\nPage unchanged since browser call {callId}.";
    if (ReferenceEquals(changes, snapshot))
      return $"{header}\n{snapshot}";

    const string previousAction = "Changed since the previous action:\n";
    string body = changes.StartsWith(previousAction, StringComparison.Ordinal)
        ? changes[previousAction.Length..] : changes;
    string diff = $"{header}\nChanged since browser call {callId}:\n{body}";
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
