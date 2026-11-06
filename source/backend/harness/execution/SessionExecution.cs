using System.Runtime.ExceptionServices;
using Microsoft.Extensions.AI;
using Urd.Core.Sessions;

namespace Urd.Core.Execution;

internal sealed class SessionExecution
{
    private readonly SessionBase _owner;
    private readonly IChatClient _chatClient; // Borrowed; the composing owner controls its lifetime.
    private readonly CancellationTokenSource _runStop = new();
    private Task? _cancellationCallbacks;

    internal SessionExecution(Guid runId, SessionBase owner, IChatClient chatClient)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(chatClient);
        RunId = runId;
        _owner = owner;
        _chatClient = chatClient;
    }

    internal Guid RunId { get; }

    // Lifecycle members are called only by SessionBase while it holds the session guard.
    internal Task RequestCancellationAsync() => _cancellationCallbacks ??= _runStop.CancelAsync();

    internal Task CancellationCompletion => _cancellationCallbacks ?? Task.CompletedTask;

    // The owner calls this only after RunAsync and cancellation callbacks have finished
    // and the durable run settlement has been saved.
    internal void ReleaseAfterSettlement() => _runStop.Dispose();

    // The sole owner invokes this once, outside the session guard.
    internal async Task<ExecutionOutcome> RunAsync()
    {
        LlmRequest? request = await _owner.ClaimNextRequestAsync(RunId).ConfigureAwait(false);
        if (request is null)
        {
            return _runStop.IsCancellationRequested
                ? ExecutionOutcome.Cancelled
                : throw new InvalidOperationException("The active run had no claimable request and was not stopped.");
        }

        // Disposing the linked source can wait for a running cancellation callback.
        // Keep it alive until after request completion has been recorded.
        using var requestStop = CancellationTokenSource.CreateLinkedTokenSource(_runStop.Token);
        var writer = new LlmResponseWriter(request.RequestId, WriteResponseChunkAsync, _runStop.Token);
        LlmRequestOutcome completion = await StreamResponseAsync(request, requestStop, writer).ConfigureAwait(false);
        bool recorded = await _owner.CompleteRequestAsync(RunId, completion).ConfigureAwait(false);
        return recorded ? completion.Outcome : ExecutionOutcome.Cancelled;
    }

    private async Task<LlmRequestOutcome> StreamResponseAsync(LlmRequest request,
        CancellationTokenSource requestStop, LlmResponseWriter writer)
    {
        IAsyncEnumerator<ChatResponseUpdate>? source = null;
        Task<bool>? pendingRead = null;
        Exception? primaryFailure = null;
        try
        {
            _runStop.Token.ThrowIfCancellationRequested();
            source = _chatClient.GetStreamingResponseAsync(request.Messages, cancellationToken: requestStop.Token)
                .GetAsyncEnumerator(requestStop.Token);
            bool sawStopFinishReason = false;
            while (true)
            {
                pendingRead ??= source.MoveNextAsync().AsTask();
                Task? flushDelay = writer.FlushDelay;
                if (flushDelay is { IsCompleted: true }
                    || (flushDelay is not null
                        && await Task.WhenAny(pendingRead, flushDelay).ConfigureAwait(false) == flushDelay))
                {
                    _runStop.Token.ThrowIfCancellationRequested();
                    await writer.FlushAsync().ConfigureAwait(false);
                    continue; // Retain the same pending read when the flush delay wins.
                }

                Task<bool> readTask = pendingRead;
                pendingRead = null;
                if (!await readTask.ConfigureAwait(false))
                {
                    break;
                }

                _runStop.Token.ThrowIfCancellationRequested();
                sawStopFinishReason = await AppendUpdateAsync(source.Current, writer, sawStopFinishReason)
                    .ConfigureAwait(false);
            }

            _runStop.Token.ThrowIfCancellationRequested();
            if (!sawStopFinishReason)
            {
                throw new InvalidDataException("The fake-text response lacks terminal success.");
            }

            await writer.FinishResponseAsync().ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            primaryFailure = failure;
        }

        // Cleanup always runs before classifying the response. Its failures must reach
        // the owner, not become an ordinary failed request that permits settlement.
        await CleanupResponseAsync(requestStop, source, pendingRead, writer, primaryFailure).ConfigureAwait(false);
        return GetRequestOutcome(request.RequestId, primaryFailure);
    }

    private static async Task<bool> AppendUpdateAsync(ChatResponseUpdate update,
        LlmResponseWriter writer, bool sawStopFinishReason)
    {
        foreach (AIContent content in update.Contents)
        {
            if (content is UsageContent)
            {
                continue;
            }

            if (content is not TextContent text)
            {
                throw new InvalidDataException("The fake-text execution received non-text or error content.");
            }

            if (sawStopFinishReason && text.Text.Length != 0)
            {
                throw new InvalidDataException("Text arrived after the terminal response marker.");
            }

            await writer.AppendTextAsync(text.Text).ConfigureAwait(false);
        }

        if (update.FinishReason is null)
        {
            return sawStopFinishReason;
        }

        if (update.FinishReason != ChatFinishReason.Stop)
        {
            throw new InvalidDataException("The fake-text request did not finish successfully.");
        }

        return true;
    }

    private LlmRequestOutcome GetRequestOutcome(Guid requestId, Exception? failure)
    {
        if (failure is OwnerOperationException)
        {
            ExceptionDispatchInfo.Throw(failure);
        }

        ExecutionOutcome outcome = failure switch
        {
            null => _runStop.IsCancellationRequested ? ExecutionOutcome.Cancelled : ExecutionOutcome.Completed,
            FencedExecutionException => ExecutionOutcome.Cancelled,
            OperationCanceledException when _runStop.IsCancellationRequested => ExecutionOutcome.Cancelled,
            _ => ExecutionOutcome.Failed
        };
        return new(requestId, outcome, outcome == ExecutionOutcome.Failed ? failure!.Message : null);
    }

    private async Task WriteResponseChunkAsync(LlmResponseChunk chunk)
    {
        bool recorded;
        try
        {
            recorded = await _owner.RecordOutputAsync(RunId, chunk).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            throw new OwnerOperationException(error);
        }

        if (!recorded)
        {
            throw new FencedExecutionException();
        }
    }

    private static async Task CleanupResponseAsync(CancellationTokenSource requestStop,
        IAsyncEnumerator<ChatResponseUpdate>? source, Task<bool>? pendingRead,
        LlmResponseWriter writer, Exception? primaryFailure)
    {
        Exception? cancellationFailure = await CaptureCleanupFailureAsync(() => new ValueTask(requestStop.CancelAsync()))
            .ConfigureAwait(false);
        if (pendingRead is not null)
        {
            // The abandoned read may fail on cancellation. Join it before disposing
            // its enumerator, but do not treat its failure as a cleanup failure.
            _ = await CaptureCleanupFailureAsync(() => new ValueTask(pendingRead)).ConfigureAwait(false);
        }

        Exception? sourceFailure = source is null ? null
            : await CaptureCleanupFailureAsync(source.DisposeAsync).ConfigureAwait(false);
        Exception? writerFailure = await CaptureCleanupFailureAsync(writer.DisposeAsync).ConfigureAwait(false);
        if (cancellationFailure is null && sourceFailure is null && writerFailure is null)
        {
            return;
        }

        throw new AggregateException("Execution cleanup could not be confirmed.",
            new[] { primaryFailure, cancellationFailure, sourceFailure, writerFailure }.OfType<Exception>());
    }

    private static async Task<Exception?> CaptureCleanupFailureAsync(Func<ValueTask> cleanup)
    {
        try
        {
            await cleanup().ConfigureAwait(false);
            return null;
        }
        catch (Exception failure)
        {
            return failure;
        }
    }

    private sealed class FencedExecutionException : Exception { }
    private sealed class OwnerOperationException(Exception inner) : Exception("Recording output failed.", inner) { }
}
