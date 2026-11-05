using System.Buffers;
using System.Text;
using Microsoft.Extensions.AI;
using Urd.Core.Sessions;

namespace Urd.Core.Execution;

internal sealed class SessionExecution
{
    private const int BatchByteLimit = 16 * 1024;
    private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(100);
    private readonly SessionBase _owner;
    private readonly IChatClient _chatClient;
    private readonly CancellationTokenSource _stop = new();
    private readonly StringBuilder _outputBatch = new();
    private readonly object _lifetimeGate = new();
    private TaskCompletionSource? _cancellationFinished;
    private bool _running, _released;
    private int _batchUtf8Bytes;
    private char? _pendingHighSurrogate;
    private Guid _requestId, _outputId;
    private long _batchIndex;
    private CancellationTokenSource? _deadlineCancellation;
    private Task? _deadline;

    internal SessionExecution(Guid runId, SessionBase owner, IChatClient chatClient)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(chatClient);
        RunId = runId;
        _owner = owner;
        _chatClient = chatClient;
    }

    internal Guid RunId { get; }

    internal Task<ExecutionOutcome> RunAsync()
    {
        lock (_lifetimeGate)
        {
            ObjectDisposedException.ThrowIf(_released, this);
            if (_running)
            {
                throw new InvalidOperationException("The execution already has an active invocation.");
            }

            _running = true;
        }

        return RunCoreAsync();
    }

    internal void RequestCancellation()
    {
        TaskCompletionSource finished;
        lock (_lifetimeGate)
        {
            if (_released || _cancellationFinished is not null)
            {
                return;
            }

            finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _cancellationFinished = finished;
        }

        // Callbacks must not run under the lifecycle lock (or the owner's mutation guard).
        try
        {
            _stop.Cancel();
        }
        finally
        {
            lock (_lifetimeGate)
            {
                finished.TrySetResult();
                if (_released)
                {
                    _stop.Dispose();
                }
            }
        }
    }

    // The owner confirms durable settlement before calling this; task exit alone is insufficient.
    internal void ReleaseAfterSettlement()
    {
        lock (_lifetimeGate)
        {
            if (_released)
            {
                return;
            }

            if (_running)
            {
                throw new InvalidOperationException("Cannot release an execution while its invocation is active.");
            }

            _released = true;
            if (_cancellationFinished is null || _cancellationFinished.Task.IsCompleted)
            {
                _stop.Dispose();
            }
        }
    }

    private async Task<ExecutionOutcome> RunCoreAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                RequestPlan? request = await _owner.ClaimNextRequestAsync(RunId).ConfigureAwait(false);
                if (request is null)
                {
                    return _stop.IsCancellationRequested ? ExecutionOutcome.Cancelled : ExecutionOutcome.Completed;
                }

                ExecutionOutcome outcome = await RunRequestAsync(request).ConfigureAwait(false);
                if (outcome != ExecutionOutcome.Completed)
                {
                    return outcome;
                }
            }

            return ExecutionOutcome.Cancelled;
        }
        finally
        {
            // Observe cancellation already in progress before publishing invocation exit.
            while (true)
            {
                Task? cancellation;
                lock (_lifetimeGate)
                {
                    cancellation = _cancellationFinished?.Task;
                    if (cancellation is null || cancellation.IsCompleted)
                    {
                        _running = false;
                        break;
                    }
                }

                await cancellation.ConfigureAwait(false);
            }
        }
    }

    private async Task<ExecutionOutcome> RunRequestAsync(RequestPlan request)
    {
        _requestId = request.RequestId;
        _outputId = Guid.NewGuid();
        _batchIndex = 0;
        _outputBatch.Clear();
        _batchUtf8Bytes = 0;
        _pendingHighSurrogate = null;
        ExecutionOutcome outcome;
        string? error = null;
        try
        {
            await StreamRequestAsync(request).ConfigureAwait(false);
            outcome = _stop.IsCancellationRequested ? ExecutionOutcome.Cancelled : ExecutionOutcome.Completed;
        }
        catch (OwnerOperationException)
        {
            throw;
        }
        catch (ExecutionCleanupException)
        {
            throw;
        }
        catch (FencedExecutionException)
        {
            outcome = ExecutionOutcome.Cancelled;
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            outcome = ExecutionOutcome.Cancelled;
        }
        catch (Exception modelError)
        {
            outcome = ExecutionOutcome.Failed;
            error = modelError.Message;
        }

        bool recorded = await _owner.CompleteRequestAsync(RunId, new RequestCompletion(request.RequestId, outcome, error))
            .ConfigureAwait(false);
        return recorded ? outcome : ExecutionOutcome.Cancelled;
    }

    private async Task StreamRequestAsync(RequestPlan request)
    {
        using var requestStop = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        IAsyncEnumerator<ChatResponseUpdate>? source = null;
        Task<bool>? next = null;
        Exception? primaryFailure = null;
        try
        {
            _stop.Token.ThrowIfCancellationRequested();
            source = _chatClient.GetStreamingResponseAsync(request.Messages, cancellationToken: requestStop.Token)
                .GetAsyncEnumerator(requestStop.Token);
            bool terminalSuccess = false;
            while (true)
            {
                next ??= source.MoveNextAsync().AsTask();
                if (_deadline is { IsCompleted: true }
                    || (_deadline is not null && await Task.WhenAny(next, _deadline).ConfigureAwait(false) == _deadline))
                {
                    _stop.Token.ThrowIfCancellationRequested();
                    await FlushBatchAsync(isFinal: false).ConfigureAwait(false);
                    continue; // Retain the SAME pending read when the deadline wins.
                }

                Task<bool> ready = next;
                next = null;
                if (!await ready.ConfigureAwait(false))
                {
                    break;
                }

                _stop.Token.ThrowIfCancellationRequested();
                ChatResponseUpdate update = source.Current;
                foreach (AIContent content in update.Contents)
                {
                    switch (content)
                    {
                        case TextContent text:
                            if (terminalSuccess && text.Text.Length != 0)
                            {
                                throw new InvalidDataException("Text arrived after the terminal response marker.");
                            }

                            await AppendTextAsync(text.Text).ConfigureAwait(false);
                            break;
                        case UsageContent:
                            break;
                        default:
                            throw new InvalidDataException("The fake-text execution received non-text or error content.");
                    }
                }

                if (update.FinishReason is { } finish)
                {
                    if (finish != ChatFinishReason.Stop)
                    {
                        throw new InvalidDataException("The fake-text request did not finish successfully.");
                    }

                    terminalSuccess = true;
                }
            }

            _stop.Token.ThrowIfCancellationRequested();
            if (!terminalSuccess || _pendingHighSurrogate is not null)
            {
                throw new InvalidDataException("The fake-text response lacks terminal success or contains incomplete Unicode.");
            }

            await FlushBatchAsync(isFinal: true).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            primaryFailure = error;
            throw;
        }
        finally
        {
            try
            {
                await CleanupRequestAsync(requestStop, source, next).ConfigureAwait(false);
            }
            catch (Exception cleanupError)
            {
                throw new ExecutionCleanupException(primaryFailure is null
                    ? cleanupError : new AggregateException(primaryFailure, cleanupError));
            }
        }
    }

    private async Task AppendTextAsync(string text)
    {
        int offset = 0;
        if (_pendingHighSurrogate is { } high && text.Length != 0)
        {
            if (!Rune.TryCreate(high, text[0], out Rune pair))
            {
                throw new InvalidDataException("The fake-text response contains invalid Unicode.");
            }

            if (_batchUtf8Bytes + pair.Utf8SequenceLength > BatchByteLimit)
            {
                await FlushBatchAsync(isFinal: false).ConfigureAwait(false);
            }

            _outputBatch.Append(high).Append(text[0]);
            _batchUtf8Bytes += pair.Utf8SequenceLength;
            _pendingHighSurrogate = null;
            ArmDeadline();
            if (_batchUtf8Bytes == BatchByteLimit)
            {
                await FlushBatchAsync(isFinal: false).ConfigureAwait(false);
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
                throw new InvalidDataException("The fake-text response contains invalid Unicode.");
            }

            if (_batchUtf8Bytes + rune.Utf8SequenceLength > BatchByteLimit)
            {
                await FlushBatchAsync(isFinal: false).ConfigureAwait(false);
            }

            _outputBatch.Append(text.AsSpan(offset, consumed));
            _batchUtf8Bytes += rune.Utf8SequenceLength;
            offset += consumed;
            ArmDeadline();
            if (_batchUtf8Bytes == BatchByteLimit)
            {
                await FlushBatchAsync(isFinal: false).ConfigureAwait(false);
            }
        }
    }

    private void ArmDeadline()
    {
        if (_deadline is null)
        {
            _deadlineCancellation = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            _deadline = Task.Delay(FlushInterval, _deadlineCancellation.Token);
        }
    }

    private async Task FlushBatchAsync(bool isFinal)
    {
        await DisarmDeadlineAsync().ConfigureAwait(false);
        _stop.Token.ThrowIfCancellationRequested();
        if (_batchUtf8Bytes == 0 && !isFinal)
        {
            return;
        }

        bool recorded;
        try
        {
            recorded = await _owner.RecordOutputAsync(RunId,
                new OutputBatch(_requestId, _outputId, _batchIndex, _outputBatch.ToString(), isFinal)).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            throw new OwnerOperationException(error);
        }

        if (!recorded)
        {
            throw new FencedExecutionException();
        }

        _batchIndex++;
        _outputBatch.Clear();
        _batchUtf8Bytes = 0;
    }

    private async Task DisarmDeadlineAsync()
    {
        if (_deadline is null)
        {
            return;
        }

        CancellationTokenSource cancellation = _deadlineCancellation!;
        try
        {
            cancellation.Cancel();
            await _deadline.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            cancellation.Dispose();
            _deadline = null;
            _deadlineCancellation = null;
        }
    }

    private async Task CleanupRequestAsync(CancellationTokenSource requestStop,
        IAsyncEnumerator<ChatResponseUpdate>? source, Task<bool>? next)
    {
        Exception? error = null;
        try
        {
            await requestStop.CancelAsync().ConfigureAwait(false);
        }
        catch (Exception cancellationError)
        {
            error = cancellationError;
        }

        if (next is not null)
        {
            try { await next.ConfigureAwait(false); }
            catch (Exception) { /* Observe the abandoned read, then dispose only after it exits. */ }
        }

        try
        {
            if (source is not null)
            {
                await source.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (Exception disposalError)
        {
            error = error is null ? disposalError : new AggregateException(error, disposalError);
        }
        finally
        {
            await DisarmDeadlineAsync().ConfigureAwait(false);
            _outputBatch.Clear();
            _batchUtf8Bytes = 0;
            _pendingHighSurrogate = null;
        }

        if (error is not null)
        {
            throw error;
        }
    }

    private sealed class FencedExecutionException : Exception { }
    private sealed class OwnerOperationException(Exception inner) : Exception("Recording output failed.", inner) { }
    private sealed class ExecutionCleanupException(Exception inner) : Exception("Execution cleanup could not be confirmed.", inner) { }
}
