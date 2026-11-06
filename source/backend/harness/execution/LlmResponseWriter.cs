using System.Buffers;
using System.Text;

namespace Urd.Core.Execution;

// Batches this response by byte size and elapsed time before persisting chunks.
internal sealed class LlmResponseWriter : IAsyncDisposable
{
    private const int BatchByteLimit = 16 * 1024;
    private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(100);

    private readonly Guid _requestId;
    private readonly Guid _responseId = Guid.NewGuid();
    private readonly Func<LlmResponseChunk, Task> _writeChunkAsync;
    private readonly CancellationToken _cancellationToken;
    private readonly StringBuilder _buffer = new();
    private int _bufferUtf8Bytes;
    private char? _pendingHighSurrogate;
    private long _chunkIndex;
    private CancellationTokenSource? _flushDelayCancellation;
    private Task? _flushDelay;
    private bool _disposed;

    internal LlmResponseWriter(Guid requestId, Func<LlmResponseChunk, Task> writeChunkAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(writeChunkAsync);
        _requestId = requestId;
        _writeChunkAsync = writeChunkAsync;
        _cancellationToken = cancellationToken;
    }

    internal Task? FlushDelay => _flushDelay;

    internal async Task AppendAsync(string text)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(text);

        int offset = 0;
        if (_pendingHighSurrogate is { } high && text.Length != 0)
        {
            if (!Rune.TryCreate(high, text[0], out Rune pair))
            {
                throw new InvalidDataException("The LLM response contains invalid Unicode.");
            }

            if (_bufferUtf8Bytes + pair.Utf8SequenceLength > BatchByteLimit)
            {
                await FlushAsync().ConfigureAwait(false);
            }

            _buffer.Append(high).Append(text[0]);
            _bufferUtf8Bytes += pair.Utf8SequenceLength;
            _pendingHighSurrogate = null;
            StartFlushDelay();
            if (_bufferUtf8Bytes == BatchByteLimit)
            {
                await FlushAsync().ConfigureAwait(false);
            }

            offset = 1;
        }

        while (offset < text.Length)
        {
            if (char.IsHighSurrogate(text[offset]) && offset == text.Length - 1)
            {
                _pendingHighSurrogate = text[offset];
                break;
            }

            if (Rune.DecodeFromUtf16(text.AsSpan(offset), out Rune rune, out int consumed) != OperationStatus.Done)
            {
                throw new InvalidDataException("The LLM response contains invalid Unicode.");
            }

            if (_bufferUtf8Bytes + rune.Utf8SequenceLength > BatchByteLimit)
            {
                await FlushAsync().ConfigureAwait(false);
            }

            _buffer.Append(text.AsSpan(offset, consumed));
            _bufferUtf8Bytes += rune.Utf8SequenceLength;
            offset += consumed;
            StartFlushDelay();
            if (_bufferUtf8Bytes == BatchByteLimit)
            {
                await FlushAsync().ConfigureAwait(false);
            }
        }
    }

    internal async Task FlushAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await StopFlushDelayAsync().ConfigureAwait(false);
        await PublishChunkAsync(isFinal: false).ConfigureAwait(false);
    }

    internal async Task CompleteAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await StopFlushDelayAsync().ConfigureAwait(false);
        if (_pendingHighSurrogate is not null)
        {
            throw new InvalidDataException("The LLM response ends with an incomplete Unicode scalar.");
        }

        await PublishChunkAsync(isFinal: true).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await StopFlushDelayAsync().ConfigureAwait(false);
    }

    private void StartFlushDelay()
    {
        if (_flushDelay is not null)
        {
            return;
        }

        _flushDelayCancellation = CancellationTokenSource.CreateLinkedTokenSource(_cancellationToken);
        _flushDelay = Task.Delay(FlushInterval, _flushDelayCancellation.Token);
    }

    private async Task StopFlushDelayAsync()
    {
        if (_flushDelay is not { } delay)
        {
            return;
        }

        CancellationTokenSource cancellation = _flushDelayCancellation!;
        try
        {
            cancellation.Cancel();
            try
            {
                await delay.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
            }
        }
        finally
        {
            cancellation.Dispose();
            _flushDelay = null;
            _flushDelayCancellation = null;
        }
    }

    private async Task PublishChunkAsync(bool isFinal)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        if (_bufferUtf8Bytes == 0 && !isFinal)
        {
            return;
        }

        var chunk = new LlmResponseChunk(_requestId, _responseId, _chunkIndex, _buffer.ToString(), isFinal);
        await _writeChunkAsync(chunk).ConfigureAwait(false);

        _chunkIndex++;
        _buffer.Clear();
        _bufferUtf8Bytes = 0;
    }
}
