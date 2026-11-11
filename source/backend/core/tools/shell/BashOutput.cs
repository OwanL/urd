using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Urd.Core.Tools;

// A bounded UTF-8 display tail; only truncated output is copied in full to an owned temp file.
internal sealed class BashOutput : IDisposable {
  private const int MaxLines = 2000;
  private const int MaxBytes = 50 * 1024;
  private readonly Decoder decoder = Encoding.UTF8.GetDecoder();
  private readonly char[] characters = new char[4096];
  private readonly MemoryStream prefix = new();
  private readonly StringBuilder tail = new();
  private bool tailStartsAtLineBoundary = true;
  private long rawBytes;
  private long decodedBytes;
  private long completedLines;
  private long lastLineBytes;
  private long lastCompletedLineBytes;
  private bool openLine;
  private bool decodedAnyText;
  private FileStream? spill;
  private string? spillPath;
  private bool finished;
  private bool published;

  private long TotalLines => completedLines + (openLine ? 1 : 0);
  private bool Truncated => TotalLines > MaxLines || decodedBytes > MaxBytes;

  public void Append(ReadOnlyMemory<byte> bytes) {
    if (finished)
      throw new InvalidOperationException("Cannot append to finished bash output.");
    rawBytes += bytes.Length;
    Decode(bytes.Span, flush: false);
    if (spill is not null || rawBytes > MaxBytes || Truncated) {
      EnsureSpill();
      spill!.Write(bytes.Span);
    } else {
      prefix.Write(bytes.Span);
    }
  }

  public void Finish() {
    if (finished)
      return;
    Decode([], flush: true);
    if (Truncated)
      EnsureSpill();
    spill?.Dispose();
    spill = null;
    finished = true;
  }

  public string Preview() => Snapshot().Text;

  public ToolOutput Result(long durationMs, int? exitCode, bool timedOut, double? timeoutSeconds, string executable) {
    SnapshotData snapshot = Snapshot();
    string text = snapshot.Text.Length == 0 ? "(no output)" : snapshot.Text;
    if (Truncated) {
      long lineBytes = openLine ? lastLineBytes : lastCompletedLineBytes;
      string limitNote = snapshot.Limit == "bytes" ? " (50KB limit)" : "";
      string shown = snapshot.PartialLine
          ? $"Showing last {snapshot.Bytes} bytes of line {TotalLines} (line is {lineBytes} bytes)."
          : $"Showing lines {TotalLines - snapshot.Lines + 1}-{TotalLines} of {TotalLines}{limitNote}.";
      text += $"\n\n[{shown} Full output: {spillPath}]";
    }
    if (timedOut)
      text += string.Create(CultureInfo.InvariantCulture, $"\n\nCommand timed out after {timeoutSeconds} seconds (elapsed {durationMs / 1000.0:F2}s).");
    else if (exitCode is not (0 or null))
      text += $"\n\nCommand exited with code {exitCode}.";
    published = true;
    return new ToolOutput(text, [], JsonSerializer.SerializeToElement(new {
      durationMs, exitCode, timedOut, fullOutputPath = spillPath, executable,
      truncation = new {
        truncated = Truncated, truncatedBy = snapshot.Limit, totalLines = TotalLines, totalBytes = decodedBytes,
        outputLines = snapshot.Lines, outputBytes = snapshot.Bytes, lastLinePartial = snapshot.PartialLine,
        maxLines = MaxLines, maxBytes = MaxBytes
      }
    }), IsError: timedOut || exitCode is not (0 or null));
  }

  public ToolOutput Failure(Exception failure, long durationMs, string executable) {
    string text = Preview();
    text += $"\n\nbash failed: {failure.Message}";
    if (spillPath is not null)
      text += $"\n[Output spill may be incomplete: {spillPath}]";
    published = true;
    return new ToolOutput(text.TrimStart('\n'), [], JsonSerializer.SerializeToElement(new {
      durationMs, executable, partialOutputPath = spillPath, outputComplete = false
    }), IsError: true);
  }

  public void Dispose() {
    spill?.Dispose();
    spill = null;
    prefix.Dispose();
    // Cancelled/fenced calls have no model-visible result; do not retain an undiscoverable raw log.
    if (!published && spillPath is not null)
      File.Delete(spillPath);
  }

  private void Decode(ReadOnlySpan<byte> bytes, bool flush) {
    bool complete;
    do {
      decoder.Convert(bytes, characters, flush, out int consumed, out int written, out complete);
      bytes = bytes[consumed..];
      if (written != 0)
        AppendText(new string(characters, 0, written));
    } while (!complete);
  }

  private void AppendText(string text) {
    if (!decodedAnyText) {
      decodedAnyText = true;
      if (text[0] == '\uFEFF')
        text = text[1..];
    }
    decodedBytes += Encoding.UTF8.GetByteCount(text);
    tail.Append(text);
    if (tail.Length > MaxBytes * 4) {
      int start = tail.Length - MaxBytes * 2;
      if (char.IsLowSurrogate(tail[start]))
        start++;
      tailStartsAtLineBoundary = tail[start - 1] == '\n';
      tail.Remove(0, start);
    }
    int startOfLine = 0;
    for (int index = text.IndexOf('\n'); index >= 0; index = text.IndexOf('\n', index + 1)) {
      lastCompletedLineBytes = lastLineBytes + Encoding.UTF8.GetByteCount(text.AsSpan(startOfLine, index - startOfLine));
      completedLines++;
      lastLineBytes = 0;
      startOfLine = index + 1;
    }
    lastLineBytes += Encoding.UTF8.GetByteCount(text.AsSpan(startOfLine));
    openLine = lastLineBytes != 0;
  }

  private SnapshotData Snapshot() {
    string text = tail.ToString();
    if (!tailStartsAtLineBoundary) {
      int newline = text.IndexOf('\n');
      if (newline >= 0 && newline < text.Length - 1)
        text = text[(newline + 1)..];
    }
    if (!Truncated)
      return new(text, null, checked((int)TotalLines), checked((int)decodedBytes), false);
    string[] lines = text.Split('\n');
    int end = lines.Length - (text.EndsWith('\n') ? 1 : 0);
    int start = end;
    int bytes = 0;
    string limit = "lines";
    bool partial = false;
    while (start > 0 && end - start < MaxLines) {
      int nextBytes = Encoding.UTF8.GetByteCount(lines[start - 1]) + (start < end ? 1 : 0);
      if (bytes + nextBytes > MaxBytes) {
        limit = "bytes";
        if (start == end) {
          byte[] lastLine = Encoding.UTF8.GetBytes(lines[--start]);
          int offset = lastLine.Length - MaxBytes;
          while (offset < lastLine.Length && (lastLine[offset] & 0xc0) == 0x80)
            offset++;
          lines[start] = Encoding.UTF8.GetString(lastLine.AsSpan(offset));
          bytes = lastLine.Length - offset;
          partial = true;
        }
        break;
      }
      bytes += nextBytes;
      start--;
    }
    if (end - start >= MaxLines)
      limit = "lines";
    else if (start == 0 && !partial)
      limit = decodedBytes > MaxBytes ? "bytes" : "lines";
    return new(string.Join("\n", lines, start, end - start), limit, end - start, bytes, partial);
  }

  private void EnsureSpill() {
    if (spill is not null)
      return;
    spillPath = Path.Combine(Path.GetTempPath(), $"urd-bash-{Guid.NewGuid():N}.log");
    spill = new FileStream(spillPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
    prefix.Position = 0;
    prefix.CopyTo(spill);
    prefix.SetLength(0);
  }

  // These are display facts, not a second copy of the model's call arguments.
  private sealed record SnapshotData(string Text, string? Limit, int Lines, int Bytes, bool PartialLine);
}
