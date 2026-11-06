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

        return await ExecuteRequestAsync(request).ConfigureAwait(false);
    }

    private async Task<ExecutionOutcome> ExecuteRequestAsync(LlmRequest request)
    {
        using var requestStop = CancellationTokenSource.CreateLinkedTokenSource(_runStop.Token);
        var writer = new LlmResponseWriter(request.RequestId, WriteResponseChunkAsync, _runStop.Token);
        ExecutionOutcome outcome;
        string? error = null;
        try
        {
            await StreamResponseAsync(request, requestStop, writer).ConfigureAwait(false);
            outcome = _runStop.IsCancellationRequested ? ExecutionOutcome.Cancelled : ExecutionOutcome.Completed;
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
        catch (OperationCanceledException) when (_runStop.IsCancellationRequested)
        {
            outcome = ExecutionOutcome.Cancelled;
        }
        catch (Exception providerError)
        {
            outcome = ExecutionOutcome.Failed;
            error = providerError.Message;
        }

        bool recorded = await _owner.CompleteRequestAsync(RunId,
            new LlmRequestOutcome(request.RequestId, outcome, error)).ConfigureAwait(false);
        return recorded ? outcome : ExecutionOutcome.Cancelled;
    }

    private async Task StreamResponseAsync(LlmRequest request,
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
                ChatResponseUpdate update = source.Current;
                foreach (AIContent content in update.Contents)
                {
                    switch (content)
                    {
                        case TextContent text:
                            if (sawStopFinishReason && text.Text.Length != 0)
                            {
                                throw new InvalidDataException("Text arrived after the terminal response marker.");
                            }

                            await writer.AppendAsync(text.Text).ConfigureAwait(false);
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

                    sawStopFinishReason = true;
                }
            }

            _runStop.Token.ThrowIfCancellationRequested();
            if (!sawStopFinishReason)
            {
                throw new InvalidDataException("The fake-text response lacks terminal success.");
            }

            await writer.CompleteAsync().ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            primaryFailure = failure;
            throw;
        }
        finally
        {
            try
            {
                await CleanupResponseAsync(requestStop, source, pendingRead, writer).ConfigureAwait(false);
            }
            catch (Exception cleanupFailure)
            {
                Exception combined = primaryFailure is null
                    ? cleanupFailure
                    : new AggregateException(primaryFailure, cleanupFailure);
                throw new ExecutionCleanupException(combined);
            }
        }
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
        IAsyncEnumerator<ChatResponseUpdate>? source, Task<bool>? pendingRead, LlmResponseWriter writer)
    {
        Exception? cleanupFailure = null;
        try
        {
            await requestStop.CancelAsync().ConfigureAwait(false);
        }
        catch (Exception cancellationFailure)
        {
            cleanupFailure = cancellationFailure;
        }

        if (pendingRead is not null)
        {
            try
            {
                await pendingRead.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Observe the abandoned read and wait for it to exit before disposing its enumerator.
            }
        }

        try
        {
            if (source is not null)
            {
                await source.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (Exception disposalFailure)
        {
            cleanupFailure = CombineFailures(cleanupFailure, disposalFailure);
        }

        try
        {
            await writer.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception disposalFailure)
        {
            cleanupFailure = CombineFailures(cleanupFailure, disposalFailure);
        }

        if (cleanupFailure is not null)
        {
            throw cleanupFailure;
        }
    }

    private static Exception CombineFailures(Exception? existing, Exception additional) =>
        existing is null ? additional : new AggregateException(existing, additional);

    private sealed class FencedExecutionException : Exception { }
    private sealed class OwnerOperationException(Exception inner) : Exception("Recording output failed.", inner) { }
    private sealed class ExecutionCleanupException(Exception inner) : Exception("Execution cleanup could not be confirmed.", inner) { }
}
