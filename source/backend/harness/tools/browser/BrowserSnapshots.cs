using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Playwright;

namespace Urd.Core.Tools;

// A full-page capture from a sequential foreground action can serve as the baseline,
// even if the shared output cap spills the result. Scoped and otherwise unstable captures are
// views only; they never replace the baseline.
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
      // A scoped subtree shares the full view's spill treatment; it never becomes a baseline.
      return await FullView(header, subtree, cancellation).ConfigureAwait(false);
    }

    string url = page.Url;
    int document = interaction.Document;
    string snapshot = await page.AriaSnapshotAsync(new() { Mode = AriaSnapshotMode.Ai })
        .WaitAsync(cancellation).ConfigureAwait(false);
    string? view = null;
    lock (gate) {
      Capture? current = capture.Value;
      bool stable = current is not null && !current.Independent && current.Generation == generation
          && document == interaction.Document && url == page.Url;
      if (stable) {
        if (!full && pages.TryGetValue(page, out Baseline? before) && before.Url == url
            && before.Document == document)
          view = ChooseView(header, snapshot, SnapshotTree.Changes(before.Text, snapshot), before.CallId);
        // A stable capture is the next baseline even when its view spills; an unstable capture
        // must never become one, so diffs are never read against an unordered or torn view.
        pages.AddOrUpdate(page, new Baseline(url, document, snapshot, current!.CallId));
      }
    }
    if (view is not null)
      return view;
    return await FullView(header, snapshot, cancellation).ConfigureAwait(false);
  }

  private static string? ChooseView(string header, string snapshot, string? changes, string callId) {
    if (changes is null)
      return $"{header}\nPage unchanged since browser call {callId}.";
    if (ReferenceEquals(changes, snapshot))
      return null;

    const string previousAction = "Changed since the previous action:\n";
    string body = changes.StartsWith(previousAction, StringComparison.Ordinal)
        ? changes[previousAction.Length..] : changes;
    string diff = $"{header}\nChanged since browser call {callId}:\n{body}";
    bool compact = Encoding.UTF8.GetByteCount(changes) < Encoding.UTF8.GetByteCount(snapshot) / 2;
    bool diffFits = ToolOutputLimit.Fits(diff);
    if (compact && diffFits)
      return diff;

    // A compact diff wins outright; otherwise prefer a complete inline view to spilling, or the
    // diff while it is substantially smaller even when both views exceed the shared cap.
    return (compact || diffFits) && !ToolOutputLimit.Fits($"{header}\n{snapshot}") ? diff : null;
  }

  // Views stay inline while the whole capture fits the shared cap, whether full-page or scoped
  // to a subtree. Once oversized, the complete capture is saved unchanged to a temp file once,
  // and the deepest uniform tree depth that fits next to the file notice, the cut notice and
  // the prelude is shown instead. A depth view is only chosen when deeper content had to be
  // cut, so the cut notice is always accurate. When the capture does not parse or no depth
  // fits, the raw view goes out and the shared output cap truncates it over the same saved
  // file, which the truncation notice already covers.
  private static async Task<string> FullView(string header, string snapshot, CancellationToken cancellation) {
    string view = $"{header}\n{snapshot}";
    if (ToolOutputLimit.Fits(view))
      return view;
    string path = Path.Combine(Path.GetTempPath(), $"urd-browser-snapshot-{Guid.NewGuid():N}.txt");
    try {
      await File.WriteAllTextAsync(path, view, cancellation).ConfigureAwait(false);
    } catch {
      File.Delete(path);
      throw;
    }
    string notice = $"[Output truncated. Full output: {path}]\n";
    string cut = "[Snapshot cut. Use snapshot ref= with a kept node's ref to inspect omitted content.]\n";
    int noticeLines = notice.Count('\n') + cut.Count('\n');
    long noticeBytes = Encoding.UTF8.GetByteCount(notice) + Encoding.UTF8.GetByteCount(cut);
    int preludeLines = header.Length == 0 ? 0 : header.Count('\n') + 1;
    long preludeBytes = Encoding.UTF8.GetByteCount(header) + (header.Length == 0 ? 0 : 1);
    // Both notices and the prelude are budgeted into the depth selection so the composed view
    // still fits the shared cap.
    string? depthView = SnapshotTree.DepthView(snapshot,
        ToolOutputLimits.MaxBytes - noticeBytes - preludeBytes,
        ToolOutputLimits.MaxLines - noticeLines - preludeLines);
    if (depthView is null)
      return ToolOutputLimit.ReuseSpill("browser", path, new ToolOutput(view, [])).Text;
    return $"{notice}{cut}{header}{(header.Length == 0 ? "" : "\n")}{depthView}";
  }
}
