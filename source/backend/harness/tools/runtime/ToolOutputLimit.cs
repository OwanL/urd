using System.Text;

namespace Urd.Core.Tools;

// Limits model-facing text before pruning, retaining the original in a readable temp file.
internal static class ToolOutputLimit {
  internal static async Task<ToolOutput> ApplyAsync(string name, ToolOutput output) {
    if (Fits(output.Text))
      return output;

    string? path = output.Spill?.Path;
    bool existing = path is not null;
    if (path is null) {
      path = Path.Combine(Path.GetTempPath(), $"urd-tool-output-{Guid.NewGuid():N}.txt");
      bool created = false;
      try {
        await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
          8192, FileOptions.Asynchronous);
        created = true;
        await using var writer = new StreamWriter(file, new UTF8Encoding(false));
        await writer.WriteAsync(output.Text).ConfigureAwait(false);
      } catch {
        if (created)
          File.Delete(path);
        throw;
      }
    }
    return Truncate(output, path, existing);
  }

  // Applies the shared cap to a result whose complete text is already saved at path, so tools
  // that spill locally share one saved file and the same truncate shape as the shared path.
  internal static ToolOutput ReuseSpill(string name, string path, ToolOutput output) =>
    Fits(output.Text) ? output : Truncate(output, path, existing: true);

  private static ToolOutput Truncate(ToolOutput output, string path, bool existing) {
    string notice = $"[Output truncated. Full output: {path}]\n";
    ToolOutputSpill? spill = existing ? output.Spill : null;
    string preview = spill?.PreviewEnd is int end ? output.Text[..end] : output.Text;
    string suffix = spill?.Suffix ?? "";
    int remainingBytes = ToolOutputLimits.MaxBytes - Encoding.UTF8.GetByteCount(notice);
    suffix = suffix[..PrefixLength(suffix, remainingBytes, ToolOutputLimits.MaxLines - 1)];
    int previewBytes = remainingBytes - Encoding.UTF8.GetByteCount(suffix);
    int previewLines = ToolOutputLimits.MaxLines - 1 - suffix.Count(character => character == '\n') - (suffix.Length == 0 ? 0 : 1);
    string selected = spill?.Tail == true
      ? preview[TailStart(preview, previewBytes, previewLines)..]
      : preview[..PrefixLength(preview, previewBytes, previewLines)];
    return output with { Text = notice + selected + suffix, Spill = new ToolOutputSpill(path) };
  }

  internal static bool Fits(string text) =>
    text.Length <= ToolOutputLimits.MaxBytes && Encoding.UTF8.GetByteCount(text) <= ToolOutputLimits.MaxBytes &&
    text.AsSpan().Count('\n') + (text.Length != 0 && text[^1] != '\n' ? 1 : 0) <= ToolOutputLimits.MaxLines;

  // Count lines as nonempty split segments plus empty lines, excluding a terminal empty segment.
  private static int PrefixLength(string text, int maxBytes, int maxLines) {
    int bytes = 0;
    int lines = 0;
    bool lineStart = true;
    int index = 0;
    while (index < text.Length) {
      int width = char.IsHighSurrogate(text[index]) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]) ? 2 : 1;
      int nextBytes = Encoding.UTF8.GetByteCount(text.AsSpan(index, width));
      if (bytes + nextBytes > maxBytes || (lineStart && lines >= maxLines))
        break;
      if (lineStart)
        lines++;
      lineStart = text[index] == '\n';
      bytes += nextBytes;
      index += width;
    }
    return index;
  }

  private static int TailStart(string text, int maxBytes, int maxLines) {
    int bytes = 0;
    int lines = text.Length > 0 && text[^1] != '\n' ? 1 : 0;
    int index = text.Length;
    while (index > 0) {
      int width = char.IsLowSurrogate(text[index - 1]) && index > 1 && char.IsHighSurrogate(text[index - 2]) ? 2 : 1;
      int nextBytes = Encoding.UTF8.GetByteCount(text.AsSpan(index - width, width));
      int nextLines = lines + (text[index - 1] == '\n' ? 1 : 0);
      if (bytes + nextBytes > maxBytes || nextLines > maxLines)
        break;
      bytes += nextBytes;
      lines = nextLines;
      index -= width;
    }
    return index;
  }

}
