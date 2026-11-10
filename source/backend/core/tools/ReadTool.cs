using System.Text;
using System.Text.Json;

namespace Urd.Core.Tools;

// Reads bounded text pages or processed images after preceding Urd mutations finish.
internal sealed class ReadTool(FileMutationQueue files) : AgentTool {
  private const int maxLines = 2000;
  private const int maxBytes = 50 * 1024;
  private const int maxImageSourceBytes = 64 * 1024 * 1024;
  private static readonly SemaphoreSlim imageReads = new(2);
  private static readonly UTF8Encoding utf8 = new(false, true);
  private static readonly byte[] jpegSignature = [0xFF, 0xD8, 0xFF];
  private static readonly byte[] pngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
  private static readonly JsonElement schema = JsonSerializer.SerializeToElement(new {
    type = "object",
    properties = new {
      path = new { type = "string", description = "Path to the file to read (relative or absolute)" },
      offset = new { type = "integer", minimum = 1, description = "Line number to start reading from (1-indexed). Omit to start at line 1." },
      limit = new { type = "integer", minimum = 1, description = "Maximum number of lines to read" }
    },
    required = new[] { "path" }
  });

  public override string Name => "read";
  public override string Description => "Read a file. Supports UTF-8 text and images (jpg, png, gif, webp, bmp). Images are processed to at most 2000×2000 and below 4.5MiB of base64; BMP is converted before attachment. Image source files are limited to 64MiB and decoding is memory-bounded. Text output is limited to 2000 lines or 50KB, whichever comes first. Use offset/limit to page through large files; continue with the suggested offset until complete.";
  public override JsonElement ParametersSchema => schema;

  public override async Task<ToolOutput> RunAsync(JsonElement arguments, ToolContext context, CancellationToken cancellation) {
    try {
      string path = arguments.GetProperty("path").GetString() ?? throw new ArgumentException("path must be a string.");
      int offset = PositiveInteger(arguments, "offset", 1);
      int limit = PositiveInteger(arguments, "limit", int.MaxValue);
      string absolutePath = ToolPaths.Resolve(path, context.WorkingDirectory);
      // Admission precedes any I/O await, so reads observe earlier mutations in the reply.
      return await files.ReadAsync(absolutePath, token => ReadAsync(absolutePath, offset, limit, context, token), cancellation).ConfigureAwait(false);
    } catch (OperationCanceledException) {
      return new ToolOutput("read cancelled.", [], IsError: true);
    } catch (DecoderFallbackException) {
      return new ToolOutput("read failed: file is not valid UTF-8 text or a supported image. Use bash for binary inspection.", [], IsError: true);
    } catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or KeyNotFoundException or NotSupportedException) {
      return new ToolOutput($"read failed: {error.Message}", [], IsError: true);
    }
  }

  private static int PositiveInteger(JsonElement arguments, string name, int fallback) {
    if (!arguments.TryGetProperty(name, out JsonElement value))
      return fallback;
    if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int number) || number < 1)
      throw new ArgumentException($"{name} must be a positive integer.");
    return number;
  }

  private static async Task<ToolOutput> ReadAsync(string path, int offset, int limit, ToolContext context, CancellationToken cancellation) {
    await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
      8192, FileOptions.Asynchronous | FileOptions.SequentialScan);
    byte[] header = new byte[32];
    int headerLength = await stream.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, cancellation).ConfigureAwait(false);
    string? mime = ImageMime(header.AsSpan(0, headerLength));
    stream.Position = 0;
    if (mime is null)
      return await ReadTextAsync(stream, path, offset, limit, cancellation).ConfigureAwait(false);
    return await ReadImageAsync(stream, mime, context.ModelAcceptsImages, cancellation).ConfigureAwait(false);
  }

  private static async Task<ToolOutput> ReadImageAsync(FileStream stream, string mime, bool modelAcceptsImages,
      CancellationToken cancellation) {
    if (stream.Length > maxImageSourceBytes)
      return new ToolOutput("[Image omitted: source exceeds the 64MiB image processing limit.]", [], IsError: true);
    // Acquire before buffering, so queued image reads do not retain source-sized arrays.
    await imageReads.WaitAsync(cancellation).ConfigureAwait(false);
    try {
      using var image = new MemoryStream();
      byte[] buffer = new byte[8192];
      int count;
      while ((count = await stream.ReadAsync(buffer, cancellation).ConfigureAwait(false)) > 0) {
        // An outside writer may grow the file after the length check.
        if (image.Length + count > maxImageSourceBytes)
          return new ToolOutput("[Image omitted: source exceeds the 64MiB image processing limit.]", [], IsError: true);
        await image.WriteAsync(buffer.AsMemory(0, count), cancellation).ConfigureAwait(false);
      }
      cancellation.ThrowIfCancellationRequested();
      return await ToolImageProcessor.ProcessAsync(image.GetBuffer().AsMemory(0, (int)image.Length),
        mime, modelAcceptsImages, cancellation).ConfigureAwait(false);
    } finally {
      imageReads.Release();
    }
  }

  private static async Task<ToolOutput> ReadTextAsync(Stream stream, string path, int offset, int limit, CancellationToken cancellation) {
    using var reader = new StreamReader(stream, utf8, detectEncodingFromByteOrderMarks: false, bufferSize: 8192, leaveOpen: true);
    var page = new TextPage(path, offset, limit);
    char[] buffer = new char[8192];
    int count;
    while ((count = await reader.ReadAsync(buffer, cancellation).ConfigureAwait(false)) > 0) {
      cancellation.ThrowIfCancellationRequested();
      for (int index = 0; index < count; index++) {
        char character = buffer[index];
        if (char.IsControl(character) && character is not ('\t' or '\r' or '\n' or '\f' or '\v'))
          throw new ArgumentException("File appears to be binary, not UTF-8 text. Use bash for binary inspection.");
        page.Add(character);
      }
    }
    cancellation.ThrowIfCancellationRequested();
    return page.Finish();
  }

  private static string? ImageMime(ReadOnlySpan<byte> header) {
    if (header.StartsWith(jpegSignature) && header.Length >= 4 && header[3] != 0xF7)
      return "image/jpeg";
    if (header.StartsWith(pngSignature) && header.Length >= 16 && header[12..16].SequenceEqual("IHDR"u8))
      return "image/png";
    if (header.StartsWith("GIF87a"u8) || header.StartsWith("GIF89a"u8))
      return "image/gif";
    if (header.Length >= 12 && header[..4].SequenceEqual("RIFF"u8) && header[8..12].SequenceEqual("WEBP"u8))
      return "image/webp";
    if (header.Length >= 26 && header.StartsWith("BM"u8)) {
      uint dibSize = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(header[14..]);
      int planesOffset = dibSize == 12 ? 22 : 26;
      if ((dibSize == 12 || dibSize is >= 40 and <= 124) && header.Length >= planesOffset + 4 &&
          System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(header[planesOffset..]) == 1 &&
          System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(header[(planesOffset + 2)..]) is 1 or 4 or 8 or 16 or 24 or 32)
        return "image/bmp";
    }
    return null;
  }

  // Only the current line and selected page are retained, even when scanning a huge file.
  private sealed class TextPage(string path, int offset, int limit) {
    private readonly char[] line = new char[maxBytes + 1];
    private readonly List<string> output = [];
    private long lineNumber = 1;
    private int lineLength;
    private int outputBytes;
    private string? cutoff;
    private bool oversized;

    public void Add(char character) {
      if (character == '\n') {
        CompleteLine(endOfFile: false);
        lineNumber++;
        lineLength = 0;
        return;
      }
      if (lineLength < line.Length)
        line[lineLength++] = character;
    }

    public ToolOutput Finish() {
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
}
