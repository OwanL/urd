using System.Text;

namespace Urd.Core.Execution;

// Gathers one response's text into chunks, saving each when it fills or has waited long enough.
// The session decides whether a chunk is still wanted; this class only sizes and times them.
internal sealed class LlmResponseWriter {
  private const int ChunkByteLimit = 16 * 1024;
  private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(100);
  private static readonly UTF8Encoding StrictUtf8 = new(false, true);

  private readonly Guid requestId;
  private readonly Guid responseId = Guid.NewGuid();
  private readonly Func<LlmResponseChunk, Task> saveChunkAsync;

  private readonly StringBuilder chunk = new();
  private int chunkUtf8Bytes;
  private long chunkIndex;
  private char? pendingHighSurrogate;

  // Completes when the oldest unsaved text is due; null while nothing is waiting.
  internal Task? FlushDelay { get; private set; }

  internal LlmResponseWriter(Guid requestId, Func<LlmResponseChunk, Task> saveChunkAsync) {
    this.requestId = requestId;
    this.saveChunkAsync = saveChunkAsync;
  }

  internal async Task AppendTextAsync(string text) {
    int offset = 0;
    while (offset < text.Length) {
      int appended = AppendSpan(text.AsSpan(offset));
      if (appended > 0) {
        offset += appended;
      } else {
        // Single units, split scalars and the last few chunk bytes use the scalar path.
        if (!TryCompleteCharacter(text[offset++], out Rune character))
          continue;

        int utf8Bytes = character.Utf8SequenceLength;
        if (chunkUtf8Bytes + utf8Bytes > ChunkByteLimit)
          await FlushAsync();

        AppendCharacter(character);
        chunkUtf8Bytes += utf8Bytes;
      }

      // Keep the oldest unsaved character's deadline until the next flush.
      if (chunkUtf8Bytes == ChunkByteLimit)
        await FlushAsync();
      else
        FlushDelay ??= Task.Delay(FlushInterval);
    }
  }

  internal Task FlushAsync() => SaveChunkAsync(isFinal: false);

  // Closes the response text, not the request or execution.
  internal Task FinishResponseAsync() => SaveChunkAsync(isFinal: true);

  private int AppendSpan(ReadOnlySpan<char> text) {
    // A lone trailing unit resolves on the scalar path, where a split pair can complete.
    if (pendingHighSurrogate is not null || text.Length < 2)
      return 0;

    // Each UTF-16 unit needs at most three UTF-8 bytes. Limit validation to text
    // guaranteed to fit, so a malformed tail cannot hide an earlier chunk-save failure.
    int length = Math.Min(text.Length, (ChunkByteLimit - chunkUtf8Bytes) / 3);
    if (length > 0 && char.IsHighSurrogate(text[length - 1]))
      length--;
    if (length == 0)
      return 0;

    ReadOnlySpan<char> portion = text[..length];
    int utf8Bytes;
    try {
      utf8Bytes = StrictUtf8.GetByteCount(portion);
    } catch (EncoderFallbackException exception) {
      throw new InvalidDataException("The LLM response contains invalid Unicode.", exception);
    }

    chunk.Append(portion);
    chunkUtf8Bytes += utf8Bytes;
    return length;
  }

  // Returns false while waiting for the second half of a surrogate pair,
  // which may arrive in a later update.
  private bool TryCompleteCharacter(char codeUnit, out Rune character) {
    if (pendingHighSurrogate is null && char.IsHighSurrogate(codeUnit)) {
      pendingHighSurrogate = codeUnit;
      character = default;
      return false;
    }

    bool valid = pendingHighSurrogate is { } high
        ? Rune.TryCreate(high, codeUnit, out character)
        : Rune.TryCreate(codeUnit, out character);
    pendingHighSurrogate = null;
    if (!valid)
      throw new InvalidDataException("The LLM response contains invalid Unicode.");

    return true;
  }

  private void AppendCharacter(Rune character) {
    Span<char> utf16 = stackalloc char[2];
    int length = character.EncodeToUtf16(utf16);
    chunk.Append(utf16[..length]);
  }

  private async Task SaveChunkAsync(bool isFinal) {
    if (isFinal && pendingHighSurrogate is not null)
      throw new InvalidDataException("The LLM response ends with an incomplete Unicode scalar.");

    FlushDelay = null;
    if (chunkUtf8Bytes == 0 && !isFinal)
      return;

    await saveChunkAsync(new LlmResponseChunk(requestId, responseId, chunkIndex, chunk.ToString(), isFinal));
    chunkIndex++;
    chunk.Clear();
    chunkUtf8Bytes = 0;
  }
}
