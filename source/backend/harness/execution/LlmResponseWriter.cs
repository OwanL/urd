using System.Text;

namespace Urd.Core.Execution;

// Batches this response by byte size and elapsed time before persisting chunks.
internal sealed class LlmResponseWriter : IAsyncDisposable
{
    private const int ChunkByteLimit = 16 * 1024;
    private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(100);

    private readonly Guid _requestId;
    private readonly Guid _responseId = Guid.NewGuid();
    private readonly Func<LlmResponseChunk, Task> _saveChunkAsync;
    private readonly CancellationToken _cancellationToken;

    private readonly StringBuilder _buffer = new();
    private int _bufferUtf8Bytes;
    private char? _pendingHighSurrogate;
    private long _chunkIndex;

    private CancellationTokenSource? _flushDelayCancellation;
    private Task? _flushDelay;
    private bool _disposed;

    internal LlmResponseWriter(Guid requestId, Func<LlmResponseChunk, Task> saveChunkAsync,
        CancellationToken cancellationToken)
    {
        _requestId = requestId;
        _saveChunkAsync = saveChunkAsync;
        _cancellationToken = cancellationToken;
    }

    internal Task? FlushDelay => _flushDelay;

    internal async Task AppendTextAsync(string text)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        foreach (char codeUnit in text)
        {
            if (TryCompleteCharacter(codeUnit, out Rune character))
            {
                await BufferCharacterAsync(character);
            }
        }
    }

    internal Task FlushAsync() => WriteChunkAsync(isFinal: false);

    // Closes the response text, not the request or execution.
    internal Task FinishResponseAsync() => WriteChunkAsync(isFinal: true);

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await CancelFlushDelayAsync();
    }

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

    private async Task BufferCharacterAsync(Rune character)
    {
        int utf8Bytes = character.Utf8SequenceLength;
        if (_bufferUtf8Bytes + utf8Bytes > ChunkByteLimit)
        {
            await FlushAsync();
        }

        Span<char> utf16 = stackalloc char[2];
        int length = character.EncodeToUtf16(utf16);
        _buffer.Append(utf16[..length]);
        _bufferUtf8Bytes += utf8Bytes;

        if (_bufferUtf8Bytes == ChunkByteLimit)
        {
            await FlushAsync();
        }
        else
        {
            ScheduleFlush();
        }
    }

    private async Task WriteChunkAsync(bool isFinal)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await CancelFlushDelayAsync();
        if (isFinal && _pendingHighSurrogate is not null)
        {
            throw new InvalidDataException("The LLM response ends with an incomplete Unicode scalar.");
        }

        _cancellationToken.ThrowIfCancellationRequested();
        if (_bufferUtf8Bytes == 0 && !isFinal)
        {
            return;
        }

        var chunk = new LlmResponseChunk(_requestId, _responseId, _chunkIndex, _buffer.ToString(), isFinal);
        await _saveChunkAsync(chunk);

        _chunkIndex++;
        _buffer.Clear();
        _bufferUtf8Bytes = 0;
    }

    // Later text must not postpone the first buffered character's deadline.
    private void ScheduleFlush()
    {
        if (_flushDelay is not null)
        {
            return;
        }

        _flushDelayCancellation = CancellationTokenSource.CreateLinkedTokenSource(_cancellationToken);
        _flushDelay = Task.Delay(FlushInterval, _flushDelayCancellation.Token);
    }

    private async Task CancelFlushDelayAsync()
    {
        if (_flushDelay is not { } delay)
        {
            return;
        }

        using CancellationTokenSource cancellation = _flushDelayCancellation!;
        _flushDelay = null;
        _flushDelayCancellation = null;
        cancellation.Cancel();
        await delay.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }
}
