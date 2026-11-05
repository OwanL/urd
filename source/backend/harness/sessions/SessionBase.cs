using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Urd.Core.Execution;
using Urd.Core.Storage;

namespace Urd.Core.Sessions;

public abstract class SessionBase
{
    private readonly SessionStore _store;
    private readonly IChatClient _chatClient;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _accepting = true;
    private SessionExecution? _execution;
    private TaskCompletionSource<ExecutionResult>? _settlement;
    private Task? _driver;

    private protected SessionBase(SessionRecord record, SessionStore store, IChatClient chatClient)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(chatClient);
        Id = record.Id;
        HistoryRevision = record.HistoryRevision;
        _store = store;
        _chatClient = chatClient;
    }

    public Guid Id { get; }

    internal long HistoryRevision { get; }

    public async Task<InputReceipt?> GetInputReceiptAsync(Guid inputId)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            return _store.ReadInputReceipt(Id, inputId);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SessionSnapshot> ReadSnapshotAsync(CancellationToken readCancellation = default)
    {
        await _gate.WaitAsync(readCancellation).ConfigureAwait(false);
        try
        {
            StoredSessionSnapshot stored = _store.ReadSnapshot(Id);
            bool fenced = !_accepting || stored.HistoryRevision != HistoryRevision
                || (stored.UnsettledRunId is { } runId && _execution?.RunId != runId);
            return new(Id, stored.HistoryRevision, fenced, stored.UnsettledRunId,
                stored.StopRequested, stored.LastSettlement, stored.Messages, stored.HasOlderMessages);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<RowContentPage> ReadRowContentAsync(
        RowContentCursor cursor, CancellationToken readCancellation = default)
    {
        await _gate.WaitAsync(readCancellation).ConfigureAwait(false);
        try
        {
            return _store.ReadRowContent(Id, cursor);
        }
        finally
        {
            _gate.Release();
        }
    }

    private protected async Task<InputReceipt> AdmitInputAsync(InputCommand command)
    {
        TaskCompletionSource? start = null;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var (receipt, newlyAccepted) = _store.AdmitInput(Id, HistoryRevision, command, _accepting, Guid.NewGuid());
            if (newlyAccepted)
            {
                // The store committed the input and its run together. Only this call
                // may start work; a retained Accepted receipt is never a start signal.
                var execution = new SessionExecution(receipt.RunId!.Value, this, _chatClient);
                var settlement = new TaskCompletionSource<ExecutionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                start = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _execution = execution;
                _settlement = settlement;
                _driver = DriveExecutionAsync(execution, settlement, start.Task);
            }

            return receipt;
        }
        catch (SqliteException)
        {
            // A failed write is not negative evidence. Keep new admission fenced
            // rather than guessing whether an input/run commit took effect.
            _accepting = false;
            throw;
        }
        finally
        {
            _gate.Release();
            start?.TrySetResult();
        }
    }

    internal async Task RequestStopAsync(Guid runId)
    {
        SessionExecution? execution;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _store.RequestStop(Id, runId);
            execution = _execution?.RunId == runId ? _execution : null;
        }
        catch (SqliteException)
        {
            _accepting = false;
            throw;
        }
        finally
        {
            _gate.Release();
        }

        // Cancellation callbacks may run synchronously. Never invoke them under
        // the session guard, and never let an old run's Stop target a newer run.
        execution?.RequestCancellation();
    }

    internal async Task<ExecutionResult> WaitForSettlementAsync(Guid runId, CancellationToken waitCancellation = default)
    {
        Task<ExecutionResult> wait;
        Task? driver;
        await _gate.WaitAsync(waitCancellation).ConfigureAwait(false);
        try
        {
            bool isCurrent = _execution?.RunId == runId;
            driver = isCurrent ? _driver : null;
            wait = isCurrent
                ? _settlement!.Task
                : Task.FromResult(_store.ReadSettlement(Id, runId)
                    ?? throw new InvalidOperationException($"Run '{runId}' has no known settlement in this handle."));
        }
        finally
        {
            _gate.Release();
        }

        if (driver is not null)
        {
            await driver.WaitAsync(waitCancellation).ConfigureAwait(false);
        }

        return await wait.WaitAsync(waitCancellation).ConfigureAwait(false);
    }

    internal async Task<LlmRequest?> ClaimNextRequestAsync(Guid runId)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            return _execution?.RunId == runId
                ? _store.ClaimNextRequest(Id, runId, HistoryRevision)
                : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task<bool> RecordOutputAsync(Guid runId, LlmResponseChunk batch)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            return _execution?.RunId == runId && _store.AppendOutput(Id, runId, HistoryRevision, batch);
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task<bool> CompleteRequestAsync(Guid runId, LlmRequestOutcome completion)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            return _execution?.RunId == runId && _store.CompleteRequest(Id, runId, HistoryRevision, completion);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task DriveExecutionAsync(
        SessionExecution execution,
        TaskCompletionSource<ExecutionResult> settlement,
        Task start)
    {
        try
        {
            // Admission publishes ownership under the guard before opening this
            // barrier. Model work and cleanup therefore never run under that guard.
            await start.ConfigureAwait(false);
            while (true)
            {
                ExecutionOutcome outcome = await execution.RunAsync().ConfigureAwait(false);
                await _gate.WaitAsync().ConfigureAwait(false);
                try
                {
                    ExecutionResult? saved = _store.TrySettle(Id, execution.RunId, outcome);
                    if (saved is null)
                    {
                        continue;
                    }

                    // RunAsync has already quiesced enumeration, timers, writes and
                    // cleanup. This release only disposes the retained run resources.
                    execution.ReleaseAfterSettlement();
                    _execution = null;
                    _settlement = null;
                    settlement.TrySetResult(saved);
                    return;
                }
                finally
                {
                    _gate.Release();
                }
            }
        }
        catch (Exception failure)
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                // No fabricated idle/settlement after uncertain persistence or
                // cleanup. Keep the execution and borrowed resources available.
                _accepting = false;
                settlement.TrySetException(failure);
                _ = settlement.Task.Exception; // Observe even if nobody is waiting yet.
            }
            finally
            {
                _gate.Release();
            }
        }
    }
}
