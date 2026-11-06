using System.Text;

namespace Urd.Core.Execution;

// Gathers one response's text into chunks, saving each when it fills or has waited long enough.
// The session decides whether a chunk is still wanted; this class only sizes and times them.
internal sealed class LlmResponseWriter
{
    private const int ChunkByteLimit = 16 * 1024;
    private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(100);

    private readonly Guid _requestId;
    private readonly Guid _responseId = Guid.NewGuid();
    private readonly Func<LlmResponseChunk, Task> _saveChunkAsync;

    private readonly StringBuilder _chunk = new();
    private int _chunkUtf8Bytes;
    private long _chunkIndex;
    private char? _pendingHighSurrogate;

    internal LlmResponseWriter(Guid requestId, Func<LlmResponseChunk, Task> saveChunkAsync)
    {
        _requestId = requestId;
        _saveChunkAsync = saveChunkAsync;
    }

    // Completes when the oldest unsaved text is due; null while nothing is waiting.
    internal Task? FlushDelay { get; private set; }

    internal async Task AppendTextAsync(string text)
    {
        foreach (char codeUnit in text)
        {
            if (TryCompleteCharacter(codeUnit, out Rune character))
            {
                await AppendCharacterAsync(character);
            }
        }
    }

    internal Task FlushAsync() => SaveChunkAsync(isFinal: false);

    // Closes the response text, not the request or execution.
    internal Task FinishResponseAsync() => SaveChunkAsync(isFinal: true);

    // Returns false while waiting for the second half of a surrogate pair,
    // which may arrive in a later update.
    private bool TryCompleteCharacter(char codeUnit, out Rune character)
    {
        if (_pendingHighSurrogate is { } high)
        {
            _pendingHighSurrogate = null;
            return Rune.TryCreate(high, codeUnit, out character) ? true : throw InvalidUnicode();
        }

        if (char.IsHighSurrogate(codeUnit))
        {
            _pendingHighSurrogate = codeUnit;
            character = default;
            return false;
        }

        return Rune.TryCreate(codeUnit, out character) ? true : throw InvalidUnicode();
    }

    private static InvalidDataException InvalidUnicode() => new("The LLM response contains invalid Unicode.");

    private async Task AppendCharacterAsync(Rune character)
    {
        int utf8Bytes = character.Utf8SequenceLength;
        if (_chunkUtf8Bytes + utf8Bytes > ChunkByteLimit)
        {
            await FlushAsync();
        }

        Span<char> utf16 = stackalloc char[2];
        _chunk.Append(utf16[..character.EncodeToUtf16(utf16)]);
        _chunkUtf8Bytes += utf8Bytes;

        if (_chunkUtf8Bytes == ChunkByteLimit)
        {
            await FlushAsync();
        }
        else
        {
            // Later text must not postpone the first unsaved character's deadline.
            FlushDelay ??= Task.Delay(FlushInterval);
        }
    }

    private async Task SaveChunkAsync(bool isFinal)
    {
        if (isFinal && _pendingHighSurrogate is not null)
        {
            throw new InvalidDataException("The LLM response ends with an incomplete Unicode scalar.");
        }

        FlushDelay = null;
        if (_chunkUtf8Bytes == 0 && !isFinal)
        {
            return;
        }

        await _saveChunkAsync(new LlmResponseChunk(_requestId, _responseId, _chunkIndex, _chunk.ToString(), isFinal));
        _chunkIndex++;
        _chunk.Clear();
        _chunkUtf8Bytes = 0;
    }
}
