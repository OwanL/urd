using System.Text;
using System.Text.Json;

namespace Urd.Core.Tools;

// Scans UTF-8 text while retaining only the current line and selected bounded page.
internal sealed class TextFilePage(string path, int offset, int limit) {
  private const int maxLines = 2000;
  private const int maxBytes = 50 * 1024;
  private static readonly UTF8Encoding utf8 = new(false, true);
  private readonly char[] line = new char[maxBytes + 1];
  private readonly List<string> output = [];
  private long lineNumber = 1;
  private int lineLength;
  private int outputBytes;
  private string? cutoff;
  private bool oversized;

  public static async Task<ToolOutput> ReadAsync(Stream stream, string path, int offset, int limit,
      CancellationToken cancellation) {
    using var reader = new StreamReader(stream, utf8, detectEncodingFromByteOrderMarks: false, bufferSize: 8192, leaveOpen: true);
    var page = new TextFilePage(path, offset, limit);
    char[] buffer = new char[8192];
    while (true) {
      int count = await reader.ReadAsync(buffer, cancellation).ConfigureAwait(false);
      if (count == 0)
        break;
      cancellation.ThrowIfCancellationRequested();
      for (int index = 0; index < count; index++)
        page.Add(buffer[index]);
    }
    cancellation.ThrowIfCancellationRequested();
    return page.Finish();
  }

  private void Add(char character) {
    if (char.IsControl(character) && character is not ('\t' or '\r' or '\n' or '\f' or '\v'))
      throw new ArgumentException("File appears to be binary, not UTF-8 text. Use bash for binary inspection.");
    if (character == '\n') {
      CompleteLine(endOfFile: false);
      lineNumber++;
      lineLength = 0;
      return;
    }
    if (lineLength < line.Length)
      line[lineLength++] = character;
  }

  private ToolOutput Finish() {
    CompleteLine(endOfFile: true);
    if (offset > lineNumber)
      throw new ArgumentException($"Offset {offset} is beyond end of file ({lineNumber} lines total).");
    string text = string.Join("\n", output);
    long nextOffset = (long)offset + output.Count;
    if (oversized) {
      string bashPath = OperatingSystem.IsWindows() ? path.Replace('\\', '/') : path;
      string quotedPath = "'" + bashPath.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
      text = $"[Line {offset} exceeds the 50KB limit. Use bash: sed -n '{offset}p' -- {quotedPath} | head -c 51200]";
    } else if (cutoff is not null) {
      text += $"\n\n[Showing lines {offset}-{nextOffset - 1} of {lineNumber}{(cutoff == "bytes" ? " (50KB limit)" : "")}. Use offset={nextOffset} to continue.]";
    } else if (nextOffset <= lineNumber) {
      text += $"\n\n[{lineNumber - nextOffset + 1} more lines in file. Use offset={nextOffset} to continue.]";
    }
    return new ToolOutput(text, [], JsonSerializer.SerializeToElement(new {
      totalLines = lineNumber, outputLines = output.Count, nextOffset = oversized ? offset : nextOffset,
      truncated = cutoff is not null, truncatedBy = cutoff, firstLineExceedsLimit = oversized,
      maxLines, maxBytes
    }));
  }

  private void CompleteLine(bool endOfFile) {
    if (lineNumber < offset || lineNumber - offset >= limit || cutoff is not null)
      return;
    // Pi counts split segments for offsets, but excludes a terminal empty one from the truncation line cap.
    if (output.Count >= maxLines && !(endOfFile && lineLength == 0)) {
      cutoff = "lines";
      return;
    }
    // The capped prefix may end halfway through a surrogate pair; never encode it.
    string text = lineLength == line.Length ? "" : new(line, 0, lineLength);
    int bytes = utf8.GetByteCount(text);
    int separatorBytes = output.Count == 0 ? 0 : 1;
    if (lineLength == line.Length || outputBytes + separatorBytes + bytes > maxBytes) {
      cutoff = "bytes";
      oversized = output.Count == 0;
      return;
    }
    output.Add(text);
    outputBytes += separatorBytes + bytes;
  }
}
