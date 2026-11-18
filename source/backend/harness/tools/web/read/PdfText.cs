using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace Urd.Core.Tools;

// PdfPig is synchronous. Limit concurrent parsers even if a caller stops waiting for one.
internal static class PdfText {
  private static readonly SemaphoreSlim parser = new(1);
  private static readonly TimeSpan deadline = TimeSpan.FromSeconds(20);

  public static async Task<(string Text, bool HasTextLayer, bool Truncated)> ReadAsync(byte[] bytes, CancellationToken cancellation) {
    using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
    limit.CancelAfter(deadline);
    try {
      await parser.WaitAsync(limit.Token).ConfigureAwait(false);
      CancellationToken token = limit.Token;
      Task<(string Text, bool HasTextLayer, bool Truncated)> work = Task.Run(() => {
        try {
          return Read(bytes, (int)WebReadLimits.MaxInputBytes, token);
        } finally {
          parser.Release();
        }
      }, CancellationToken.None);
      _ = work.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
      return await work.WaitAsync(limit.Token).ConfigureAwait(false);
    } catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) {
      throw new TimeoutException("PDF extraction did not finish in time.");
    }
  }

  internal static (string Text, bool HasTextLayer, bool Truncated) Read(byte[] bytes, int maxBytes, CancellationToken cancellation) {
    cancellation.ThrowIfCancellationRequested();
    using MemoryStream source = new(bytes);
    using PdfDocument document = PdfDocument.Open(source);
    var builder = new StringBuilder();
    int remaining = maxBytes;
    bool truncated = false;
    foreach (Page page in document.GetPages()) {
      cancellation.ThrowIfCancellationRequested();
      try {
        string text = page.Text.TrimEnd();
        int size = Encoding.UTF8.GetByteCount(text);
        if (size > remaining) {
          builder.Append(WebReadOutput.Slice(text, remaining));
          truncated = true;
          break;
        }
        builder.Append(text);
        remaining -= size;
        if (remaining == 0) {
          truncated = page.Number < document.NumberOfPages;
          break;
        }
        builder.Append('\n');
        remaining--;
      } catch (InvalidOperationException) {
        // Page-level failures lose that page's text, not the document result.
      }
    }
    string result = builder.ToString().Trim();
    return (result, result.Length > 0, truncated);
  }
}
