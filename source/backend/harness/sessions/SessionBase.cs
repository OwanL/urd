using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Urd.Core.Execution;
using Urd.Core.Storage;

namespace Urd.Core.Sessions;

// Owns one session's state. Every read and write goes through this class's lock;
// model work and stop callbacks always run outside it.
public abstract class SessionBase {
    private readonly SessionStore _store;
    private readonly IChatClient _chatClient;
    private readonly AsyncLock _lock = new();
    private bool _acceptingInput = true;
    private SessionExecution? _execution;
    private TaskCompletionSource<ExecutionResult>? _settlement;
    private Task? _executionTask;

    private protected SessionBase(SessionRecord record, SessionStore store, IChatClient chatClient) {
        Id = record.Id;
        HistoryRevision = record.HistoryRevision;
        _store = store;
        _chatClient = chatClient;
    }

    public Guid Id { get; }

    internal long HistoryRevision { get; }

    public async Task<InputReceipt?> GetInputReceiptAsync(Guid inputId) {
        using var held = await _lock.AcquireAsync();
        return _store.Inputs.ReadReceipt(Id, inputId);
    }

    public async Task<SessionSnapshot> ReadSnapshotAsync(CancellationToken cancellation = default) {
        using var held = await _lock.AcquireAsync(cancellation);
        StoredSessionSnapshot stored = _store.Snapshots.Read(Id);
        bool runOwnedElsewhere = stored.UnsettledRunId is { } runId && !IsCurrentRun(runId);
        bool fenced = !_acceptingInput || stored.HistoryRevision != HistoryRevision || runOwnedElsewhere;
        return new(Id, stored.HistoryRevision, fenced, stored.UnsettledRunId, stored.StopRequested,
            stored.LastSettlement, stored.Messages, stored.HasOlderMessages);
    }

    public async Task<MessageContentPage> ReadMessageContentAsync(
        MessageContentCursor cursor, CancellationToken cancellation = default) {
        using var held = await _lock.AcquireAsync(cancellation);
        return _store.Content.Read(Id, cursor);
    }

    private protected async Task<InputReceipt> AdmitInputAsync(InputCommand command) {
        // A new run starts only after the lock is released.
        var lockReleased = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        InputReceipt receipt;
        using (await _lock.AcquireAsync()) {
            (receipt, bool newlyAccepted) = WriteToStore(
                () => _store.Inputs.Admit(Id, HistoryRevision, command, _acceptingInput, Guid.NewGuid()));

            // Only the call that saved the acceptance may start the run; a retained
            // Accepted receipt from an earlier call must not start it again.
            if (newlyAccepted) {
                StartExecution(receipt.RunId!.Value, lockReleased.Task);
            }
        }

        lockReleased.SetResult();
        return receipt;
    }

    internal async Task RequestStopAsync(Guid runId) {
        Task stopCallbacks = Task.CompletedTask;
        using (await _lock.AcquireAsync()) {
            WriteToStore(() => _store.Runs.RequestStop(Id, runId));

            // Checked under the lock, so a stale run ID cannot stop a replacement run.
            if (IsCurrentRun(runId)) {
                stopCallbacks = _execution!.StopAsync();
            }
        }

        // Stop callbacks may call back into this session, so never await them under the lock.
        await stopCallbacks;
    }

    internal async Task<ExecutionResult> WaitForSettlementAsync(Guid runId, CancellationToken cancellation = default) {
        Task executionTask;
        Task<ExecutionResult> settlement;
        using (await _lock.AcquireAsync(cancellation)) {
            if (!IsCurrentRun(runId)) {
                return _store.Runs.ReadSettlement(Id, runId)
                    ?? throw new InvalidOperationException($"Run '{runId}' has no known settlement in this session.");
            }

            executionTask = _executionTask!;
            settlement = _settlement!.Task;
        }

        await executionTask.WaitAsync(cancellation);
        return await settlement.WaitAsync(cancellation);
    }

    internal async Task<LlmRequest?> ClaimNextRequestAsync(Guid runId) {
        using var held = await _lock.AcquireAsync();
        return IsCurrentRun(runId) ? _store.Runs.ClaimNextRequest(Id, runId, HistoryRevision) : null;
    }

    internal async Task<bool> RecordOutputAsync(Guid runId, LlmResponseChunk chunk) {
        using var held = await _lock.AcquireAsync();
        return IsCurrentRun(runId) && _store.Output.Append(Id, runId, HistoryRevision, chunk);
    }

    internal async Task<bool> CompleteRequestAsync(Guid runId, LlmRequestOutcome outcome) {
        using var held = await _lock.AcquireAsync();
        return IsCurrentRun(runId) && _store.Output.CompleteRequest(Id, runId, HistoryRevision, outcome);
    }

    // Caller holds the lock.
    private void StartExecution(Guid runId, Task lockReleased) {
        var execution = new SessionExecution(runId, this, _chatClient);
        var settlement = new TaskCompletionSource<ExecutionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _execution = execution;
        _settlement = settlement;
        _executionTask = RunExecutionAsync(execution, settlement, lockReleased);
    }

    private async Task RunExecutionAsync(SessionExecution execution,
        TaskCompletionSource<ExecutionResult> settlement, Task lockReleased) {
        try {
            await lockReleased;
            ExecutionOutcome outcome = await execution.RunAsync();
            await SettleAsync(execution, settlement, outcome);
        } catch (Exception failure) {
            // The run's saved state is uncertain, so keep it unsettled and refuse new input.
            // The execution and its resources stay in place for later recovery.
            using var held = await _lock.AcquireAsync();
            _acceptingInput = false;
            settlement.TrySetException(failure);
            _ = settlement.Task.Exception; // Observed even if nobody is waiting yet.
        }
    }

    private async Task SettleAsync(SessionExecution execution,
        TaskCompletionSource<ExecutionResult> settlement, ExecutionOutcome outcome) {
        Task stopCallbacks;
        using (await _lock.AcquireAsync()) {
            // Checking and settling under one lock stops a late Stop from skipping the wait.
            stopCallbacks = execution.StopCallbacks;
            if (stopCallbacks.IsCompletedSuccessfully) {
                Settle(execution, settlement, outcome);
                return;
            }
        }

        // Stop callbacks may call back into this session, so never await them under the lock.
        await stopCallbacks;
        using (await _lock.AcquireAsync()) {
            Settle(execution, settlement, outcome);
        }
    }

    // Caller holds the lock. Saves the result before releasing resources and
    // publishing it, so waiters only ever see a saved settlement.
    private void Settle(SessionExecution execution,
        TaskCompletionSource<ExecutionResult> settlement, ExecutionOutcome outcome) {
        ExecutionResult saved = _store.Runs.TrySettle(Id, execution.RunId, outcome)
            ?? throw new InvalidOperationException("The current run could not be settled.");

        execution.Dispose();
        _execution = null;
        _settlement = null;
        settlement.TrySetResult(saved);
    }

    private bool IsCurrentRun(Guid runId) => _execution?.RunId == runId;

    // A failed write may still have been committed, so refuse new input rather than guess.
    private T WriteToStore<T>(Func<T> write) {
        try {
            return write();
        } catch (SqliteException) {
            _acceptingInput = false;
            throw;
        }
    }

    private void WriteToStore(Action write) => WriteToStore(() => {
        write();
        return true;
    });
}
