using System.Runtime.ExceptionServices;
using Microsoft.Extensions.AI;
using Urd.Core.Sessions;

namespace Urd.Core.Execution;

// Sends one run's request to the model and streams the response into the session.
// The session owns locking and storage; this class owns the model call and its cleanup.
internal sealed class SessionExecution : IDisposable {
    private readonly SessionBase _session;
    private readonly IChatClient _chatClient; // Borrowed from the session; never disposed here.
    private readonly CancellationTokenSource _stop = new();
    private Task? _stopCallbacks;

    internal SessionExecution(Guid runId, SessionBase session, IChatClient chatClient) {
        RunId = runId;
        _session = session;
        _chatClient = chatClient;
    }

    internal Guid RunId { get; }

    // Completes once every callback registered on the stop token has finished.
    internal Task StopCallbacks => _stopCallbacks ?? Task.CompletedTask;

    // Called by the session while it holds its lock. Repeated calls share one task.
    internal Task StopAsync() => _stopCallbacks ??= _stop.CancelAsync();

    // Called by the session only after RunAsync and StopCallbacks have finished
    // and the run's result has been saved.
    public void Dispose() => _stop.Dispose();

    // Called once by the session, outside its lock.
    internal async Task<ExecutionOutcome> RunAsync() {
        LlmRequest? request = await _session.ClaimNextRequestAsync(RunId);
        if (request is null) {
            return _stop.IsCancellationRequested
                ? ExecutionOutcome.Cancelled
                : throw new InvalidOperationException("The run has no request to send and was not stopped.");
        }

        // Disposing this source waits for its running cancellation callbacks,
        // so keep it alive until the request outcome has been saved.
        using var requestStop = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        LlmRequestOutcome outcome = await SendRequestAsync(request, requestStop);
        bool saved = await _session.CompleteRequestAsync(RunId, outcome);
        return saved ? outcome.Outcome : ExecutionOutcome.Cancelled;
    }

    private async Task<LlmRequestOutcome> SendRequestAsync(LlmRequest request, CancellationTokenSource requestStop) {
        var writer = new LlmResponseWriter(request.RequestId, SaveChunkAsync);
        IAsyncEnumerator<ChatResponseUpdate>? updates = null;
        Task<bool>? pendingRead = null;
        Exception? failure = null;
        try {
            CancellationToken stop = _stop.Token;
            stop.ThrowIfCancellationRequested();
            updates = _chatClient
                .GetStreamingResponseAsync(request.Messages, cancellationToken: requestStop.Token)
                .GetAsyncEnumerator(requestStop.Token);

            bool finished = false;
            while (true) {
                pendingRead ??= updates.MoveNextAsync().AsTask();
                if (await IsFlushDueFirstAsync(writer.FlushDelay, pendingRead)) {
                    // Keep the pending read; it is awaited on the next pass.
                    stop.ThrowIfCancellationRequested();
                    await writer.FlushAsync();
                    continue;
                }

                bool hasUpdate = await pendingRead;
                pendingRead = null;
                if (!hasUpdate) {
                    break;
                }

                stop.ThrowIfCancellationRequested();
                finished = await WriteUpdateAsync(updates.Current, writer, finished);
            }

            stop.ThrowIfCancellationRequested();
            if (!finished) {
                throw new InvalidDataException("The response ended without a stop finish reason.");
            }

            await writer.FinishResponseAsync();
        } catch (Exception exception) {
            failure = exception;
        }

        await CleanUpAsync(requestStop, pendingRead, updates, failure);
        return ToRequestOutcome(request.RequestId, failure);
    }

    private static async Task<bool> IsFlushDueFirstAsync(Task? flushDelay, Task<bool> pendingRead) {
        if (flushDelay is null) {
            return false;
        }

        return flushDelay.IsCompleted || await Task.WhenAny(pendingRead, flushDelay) == flushDelay;
    }

    // Returns whether the response has finished, including this update.
    private static async Task<bool> WriteUpdateAsync(ChatResponseUpdate update,
        LlmResponseWriter writer, bool finished) {
        foreach (AIContent content in update.Contents) {
            if (content is UsageContent) {
                continue;
            }

            if (content is not TextContent text) {
                throw new InvalidDataException("The response contained content other than text.");
            }

            if (finished && text.Text.Length != 0) {
                throw new InvalidDataException("The response contained text after its stop finish reason.");
            }

            await writer.AppendTextAsync(text.Text);
        }

        if (update.FinishReason is null) {
            return finished;
        }

        return update.FinishReason == ChatFinishReason.Stop
            ? true
            : throw new InvalidDataException("The response finished without a stop finish reason.");
    }

    private LlmRequestOutcome ToRequestOutcome(Guid requestId, Exception? failure) {
        // A failed save is a session failure, not a failed model request.
        if (failure is OutputSaveException) {
            ExceptionDispatchInfo.Throw(failure);
        }

        bool stopped = _stop.IsCancellationRequested;
        ExecutionOutcome outcome = failure switch {
            null when stopped => ExecutionOutcome.Cancelled,
            null => ExecutionOutcome.Completed,
            OutputRejectedException => ExecutionOutcome.Cancelled,
            OperationCanceledException when stopped => ExecutionOutcome.Cancelled,
            _ => ExecutionOutcome.Failed
        };
        string? error = outcome == ExecutionOutcome.Failed ? failure!.Message : null;
        return new(requestId, outcome, error);
    }

    private async Task SaveChunkAsync(LlmResponseChunk chunk) {
        bool saved;
        try {
            saved = await _session.RecordOutputAsync(RunId, chunk);
        } catch (Exception exception) {
            throw new OutputSaveException(exception);
        }

        if (!saved) {
            throw new OutputRejectedException();
        }
    }

    // Attempts every step even when an earlier one fails. A cleanup failure leaves the
    // run unsettled, so it is thrown to the session instead of becoming a failed request.
    private static async Task CleanUpAsync(CancellationTokenSource requestStop, Task<bool>? pendingRead,
        IAsyncEnumerator<ChatResponseUpdate>? updates, Exception? requestFailure) {
        Exception? cancelFailure = await CaptureFailureAsync(async () => await requestStop.CancelAsync());
        if (pendingRead is not null) {
            // A read abandoned by cancellation is expected to fail. Wait for it anyway,
            // because the enumerator must be idle before it is disposed.
            _ = await CaptureFailureAsync(async () => await pendingRead);
        }

        Exception? updatesFailure = updates is null ? null : await CaptureFailureAsync(updates.DisposeAsync);
        if (cancelFailure is null && updatesFailure is null) {
            return;
        }

        Exception?[] failures = [requestFailure, cancelFailure, updatesFailure];
        throw new AggregateException("The request could not be cleaned up.", failures.OfType<Exception>());
    }

    private static async Task<Exception?> CaptureFailureAsync(Func<ValueTask> action) {
        try {
            await action();
            return null;
        } catch (Exception exception) {
            return exception;
        }
    }

    // The session no longer accepts output from this run.
    private sealed class OutputRejectedException : Exception;

    private sealed class OutputSaveException(Exception inner) : Exception("Saving response output failed.", inner);
}
