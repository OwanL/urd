using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Urd.Core.Execution;
using Urd.Core.Sessions;
using Urd.Core.Storage;
using Xunit;

namespace Urd.Backend.Tests;

public sealed class ExecutionStorageTests
{
    [Fact]
    public void AcceptanceAndRunRollbackTogetherOnCommitFailure()
    {
        using var database = new TemporaryDatabase();
        using SessionStore store = SessionStore.Open(database.Path);
        Guid session = store.CreatePrimary(Guid.NewGuid()).Id;
        var input = new InputCommand(Guid.NewGuid(), 0, "atomic input");
        Guid run = Guid.NewGuid();
        database.Execute("""
            CREATE TABLE test_parent (id INTEGER PRIMARY KEY);
            CREATE TABLE test_child (parent_id INTEGER REFERENCES test_parent(id) DEFERRABLE INITIALLY DEFERRED);
            CREATE TRIGGER fail_run AFTER INSERT ON execution_runs
            BEGIN
                INSERT INTO test_child VALUES (1);
            END;
            """);
        Assert.Throws<SqliteException>(() => store.AdmitInput(session, 0, input, true, run));
        Assert.Null(store.ReadInputReceipt(session, input.InputId));
        Assert.False(store.RunExists(session, run));
        Assert.Equal(0L, database.Scalar("SELECT COUNT(*) FROM input_decisions;"));
        Assert.Equal(0L, database.Scalar("SELECT COUNT(*) FROM execution_runs;"));
        Assert.Equal(0L, database.Scalar("SELECT COUNT(*) FROM test_child;"));

        database.Execute("DROP TRIGGER fail_run;");
        var admitted = store.AdmitInput(session, 0, input, true, run);
        Assert.True(admitted.NewlyAccepted);
        Assert.Equal(run, admitted.Receipt.RunId);
        Assert.True(store.RunExists(session, run));
        Assert.Equal(1L, database.Scalar("""
            SELECT COUNT(*) FROM input_decisions i JOIN execution_runs r
            ON r.session_id = i.session_id AND r.input_id = i.input_id AND r.run_id = i.run_id;
            """));
    }

    [Fact]
    public void RetainedAcceptanceResolvesBeforeAllCurrentGatesAndCannotCreateAnotherRun()
    {
        using var database = new TemporaryDatabase();
        Guid session = Guid.NewGuid(), run = Guid.NewGuid();
        var input = new InputCommand(Guid.NewGuid(), 0, "original \0 🙂");
        InputReceipt receipt;
        using (SessionStore store = SessionStore.Open(database.Path))
        {
            store.CreatePrimary(session);
            receipt = store.AdmitInput(session, 0, input, true, run).Receipt;
            store.RequestStop(session, run);
            Assert.Equal(ExecutionOutcome.Cancelled, store.TrySettle(session, run, ExecutionOutcome.Completed)!.Outcome);
        }

        database.Execute("UPDATE sessions SET history_revision = 7;");
        using SessionStore reopened = SessionStore.Open(database.Path);
        var retry = reopened.AdmitInput(session, 7, input, false, Guid.Empty);
        Assert.Equal(receipt, retry.Receipt);
        Assert.False(retry.NewlyAccepted);
        Assert.Null(reopened.ClaimNextRequest(session, run, 7));
        Assert.Throws<InvalidOperationException>(() => reopened.AdmitInput(session, 7, input with { Text = "changed" }, false, Guid.NewGuid()));
        Assert.Throws<InvalidOperationException>(() => reopened.AdmitInput(session, 7, input with { ExpectedHistoryRevision = 7 }, false, Guid.NewGuid()));
        Assert.Equal(1L, database.Scalar("SELECT COUNT(*) FROM execution_runs;"));
    }

    [Fact]
    public void BusyAndStoppingRefusalsAreDurableWithoutTranscriptOrRunFacts()
    {
        using var database = new TemporaryDatabase();
        using SessionStore store = SessionStore.Open(database.Path);
        Guid session = store.CreatePrimary(Guid.NewGuid()).Id, run = Guid.NewGuid();
        store.AdmitInput(session, 0, new(Guid.NewGuid(), 0, "accepted"), true, run);
        var busyInput = new InputCommand(Guid.NewGuid(), 0, "refused while busy");
        var busy = store.AdmitInput(session, 0, busyInput, true, Guid.NewGuid());
        Assert.Equal(InputRefusalReason.Capacity, busy.Receipt.RefusalReason);
        Assert.False(busy.NewlyAccepted);
        Assert.Null(busy.Receipt.RunId);
        store.RequestStop(session, run);
        var stopped = store.AdmitInput(session, 0, new(Guid.NewGuid(), 0, "refused while stopping"), true, Guid.NewGuid());
        Assert.Equal(InputRefusalReason.Stopping, stopped.Receipt.RefusalReason);
        store.TrySettle(session, run, ExecutionOutcome.Cancelled);
        Assert.Equal(busy.Receipt, store.AdmitInput(session, 0, busyInput, true, Guid.NewGuid()).Receipt);
        Guid nextRun = Guid.NewGuid();
        store.AdmitInput(session, 0, new(Guid.NewGuid(), 0, "next accepted"), true, nextRun);
        LlmRequest next = store.ClaimNextRequest(session, nextRun, 0)!;
        Assert.Equal(new[] { "accepted", "next accepted" }, next.Messages.Select(message => message.Text));
        Assert.Equal(2L, database.Scalar("SELECT COUNT(*) FROM execution_runs;"));
        Assert.Equal(4L, database.Scalar("SELECT COUNT(*) FROM input_decisions;"));
    }

    [Theory]
    [InlineData(false, false, 0, 1, InputRefusalReason.Stopping)]
    [InlineData(true, true, 0, 1, InputRefusalReason.Stopping)]
    [InlineData(true, false, 0, 1, InputRefusalReason.StaleRevision)]
    [InlineData(true, false, 1, 0, InputRefusalReason.StaleRevision)]
    [InlineData(true, false, 0, 0, InputRefusalReason.Capacity)]
    public void NewInputRefusalUsesStoppingThenRevisionThenCapacityPriority(
        bool admissionEnabled, bool stopRequested, long handleRevision,
        long expectedRevision, InputRefusalReason expectedReason)
    {
        using var database = new TemporaryDatabase();
        using SessionStore store = SessionStore.Open(database.Path);
        Guid session = store.CreatePrimary(Guid.NewGuid()).Id, run = Guid.NewGuid();
        store.AdmitInput(session, 0, new(Guid.NewGuid(), 0, "accepted"), true, run);
        if (stopRequested)
        {
            store.RequestStop(session, run);
        }

        var input = new InputCommand(Guid.NewGuid(), expectedRevision, "new input");
        var decision = store.AdmitInput(session, handleRevision, input, admissionEnabled, Guid.NewGuid());

        Assert.Equal(InputDecision.Refused, decision.Receipt.Decision);
        Assert.Equal(expectedReason, decision.Receipt.RefusalReason);
        Assert.False(decision.NewlyAccepted);
        Assert.Null(decision.Receipt.RunId);
        Assert.Equal(decision.Receipt, store.ReadInputReceipt(session, input.InputId));
        Assert.Equal(1L, database.Scalar("SELECT COUNT(*) FROM execution_runs;"));
    }

    [Fact]
    public void ClaimIsPersistedOnceAndReopenDoesNotReplayActiveInput()
    {
        using var database = new TemporaryDatabase();
        Guid session = Guid.NewGuid(), run = Guid.NewGuid(), request;
        using (SessionStore store = SessionStore.Open(database.Path))
        {
            store.CreatePrimary(session);
            store.AdmitInput(session, 0, new(Guid.NewGuid(), 0, "claimed"), true, run);
            LlmRequest plan = store.ClaimNextRequest(session, run, 0)!;
            request = plan.RequestId;
            Assert.NotEqual(Guid.Empty, request);
            Assert.Single(plan.Messages);
            Assert.Null(store.ClaimNextRequest(session, run, 0));
        }

        using SessionStore reopened = SessionStore.Open(database.Path);
        Assert.Null(reopened.ClaimNextRequest(session, run, 0));
        Assert.Null(reopened.ReadSettlement(session, run));
        Assert.Equal(request.ToString("D"), database.Scalar("SELECT request_id FROM execution_requests;"));
        Assert.Equal(InputRefusalReason.Capacity,
            reopened.AdmitInput(session, 0, new(Guid.NewGuid(), 0, "not replay or replacement"), true, Guid.NewGuid()).Receipt.RefusalReason);
    }

    [Fact]
    public void OrderedBatchesAndExplicitCompletionProduceOrderedPlainTextContext()
    {
        using var database = new TemporaryDatabase();
        using SessionStore store = SessionStore.Open(database.Path);
        Guid session = store.CreatePrimary(Guid.NewGuid()).Id, run = Guid.NewGuid(), output = Guid.NewGuid();
        store.AdmitInput(session, 0, new(Guid.NewGuid(), 0, "first \0 input"), true, run);
        LlmRequest plan = store.ClaimNextRequest(session, run, 0)!;
        var first = new LlmResponseChunk(plan.RequestId, output, 0, "part🙂", false);
        Assert.False(store.AppendOutput(session, run, 0, first with { ChunkIndex = 1 }));
        Assert.True(store.AppendOutput(session, run, 0, first));
        Assert.False(store.AppendOutput(session, run, 0, first));
        Assert.False(store.AppendOutput(session, run, 0, first with { ChunkIndex = 1, ResponseId = Guid.NewGuid() }));
        Assert.False(store.CompleteRequest(session, run, 0, new(plan.RequestId, ExecutionOutcome.Completed)));
        Assert.Null(store.TrySettle(session, run, ExecutionOutcome.Completed));
        Assert.True(store.AppendOutput(session, run, 0, first with { ChunkIndex = 1, Text = "\0 final", IsFinal = true }));
        Assert.False(store.AppendOutput(session, run, 0, first with { ChunkIndex = 2 }));
        Assert.Null(store.TrySettle(session, run, ExecutionOutcome.Completed)); // Final text is not request success.
        var completion = new LlmRequestOutcome(plan.RequestId, ExecutionOutcome.Completed);
        Assert.True(store.CompleteRequest(session, run, 0, completion));
        Assert.True(store.CompleteRequest(session, run, 0, completion));
        Assert.False(store.CompleteRequest(session, run, 0, completion with { Outcome = ExecutionOutcome.Failed }));
        Assert.Equal(new ExecutionResult(run, ExecutionOutcome.Completed), store.TrySettle(session, run, ExecutionOutcome.Completed));
        Assert.Equal(new ExecutionResult(run, ExecutionOutcome.Completed), store.TrySettle(session, run, ExecutionOutcome.Failed));

        Guid nextRun = Guid.NewGuid();
        store.AdmitInput(session, 0, new(Guid.NewGuid(), 0, "second"), true, nextRun);
        LlmRequest next = store.ClaimNextRequest(session, nextRun, 0)!;
        Assert.Equal(new[] { ChatRole.User, ChatRole.Assistant, ChatRole.User }, next.Messages.Select(message => message.Role));
        Assert.Equal(new[] { "first \0 input", "part🙂\0 final", "second" }, next.Messages.Select(message => message.Text));
        Assert.Equal(2L, database.Scalar("SELECT COUNT(*) FROM execution_output;"));
        Assert.Equal(0L, database.Scalar("SELECT outcome FROM execution_requests WHERE run_id = '" + run.ToString("D") + "';"));
    }

    [Fact]
    public void RevisionAndRunFencesRejectStaleWritesAndAdmission()
    {
        using var database = new TemporaryDatabase();
        using SessionStore store = SessionStore.Open(database.Path);
        Guid session = store.CreatePrimary(Guid.NewGuid()).Id, run = Guid.NewGuid();
        store.AdmitInput(session, 0, new(Guid.NewGuid(), 0, "current"), true, run);
        Assert.Null(store.ClaimNextRequest(session, run, 1));
        Assert.Null(store.ClaimNextRequest(Guid.NewGuid(), run, 0));
        LlmRequest plan = store.ClaimNextRequest(session, run, 0)!;
        var batch = new LlmResponseChunk(plan.RequestId, Guid.NewGuid(), 0, "late", true);
        Assert.False(store.AppendOutput(session, Guid.NewGuid(), 0, batch));
        Assert.False(store.AppendOutput(session, run, 1, batch));
        database.Execute("UPDATE sessions SET history_revision = 1;");
        Assert.False(store.AppendOutput(session, run, 0, batch));
        Assert.False(store.CompleteRequest(session, run, 0, new(plan.RequestId, ExecutionOutcome.Failed)));
        Assert.Equal(InputRefusalReason.StaleRevision,
            store.AdmitInput(session, 0, new(Guid.NewGuid(), 0, "stale"), true, Guid.NewGuid()).Receipt.RefusalReason);
        Assert.Equal(0L, database.Scalar("SELECT COUNT(*) FROM execution_output;"));
    }

    [Fact]
    public void StopRejectsLateSuccessAndOnlyFencesAddressedRun()
    {
        using var database = new TemporaryDatabase();
        using SessionStore store = SessionStore.Open(database.Path);
        Guid session = store.CreatePrimary(Guid.NewGuid()).Id, run = Guid.NewGuid();
        store.AdmitInput(session, 0, new(Guid.NewGuid(), 0, "stop"), true, run);
        LlmRequest plan = store.ClaimNextRequest(session, run, 0)!;
        var batch = new LlmResponseChunk(plan.RequestId, Guid.NewGuid(), 0, "committed before stop", true);
        Assert.True(store.AppendOutput(session, run, 0, batch));
        store.RequestStop(session, run);
        Assert.False(store.CompleteRequest(session, run, 0, new(plan.RequestId, ExecutionOutcome.Completed)));
        Assert.False(store.AppendOutput(session, run, 0, batch with { ChunkIndex = 1 }));
        Assert.True(store.CompleteRequest(session, run, 0, new(plan.RequestId, ExecutionOutcome.Cancelled)));
        Assert.Equal(ExecutionOutcome.Cancelled, store.TrySettle(session, run, ExecutionOutcome.Completed)!.Outcome);

        Guid nextRun = Guid.NewGuid();
        store.AdmitInput(session, 0, new(Guid.NewGuid(), 0, "replacement after cleanup"), true, nextRun);
        store.RequestStop(session, run);
        store.RequestStop(Guid.NewGuid(), nextRun);
        Assert.NotNull(store.ClaimNextRequest(session, nextRun, 0));
        Assert.Null(store.ReadSettlement(session, nextRun));
        Assert.Equal(ExecutionOutcome.Cancelled, store.ReadSettlement(session, run)!.Outcome);
        Assert.Null(store.TrySettle(session, Guid.NewGuid(), ExecutionOutcome.Failed));
    }

    [Fact]
    public void OutputValidationIsUtf8BoundedAndFailuresRemainTruthful()
    {
        using var database = new TemporaryDatabase();
        using SessionStore store = SessionStore.Open(database.Path);
        Guid session = store.CreatePrimary(Guid.NewGuid()).Id, run = Guid.NewGuid();
        store.AdmitInput(session, 0, new(Guid.NewGuid(), 0, "fail"), true, run);
        LlmRequest plan = store.ClaimNextRequest(session, run, 0)!;
        var batch = new LlmResponseChunk(plan.RequestId, Guid.NewGuid(), 0, new string('é', 8192), false);
        Assert.Throws<ArgumentException>(() => store.AppendOutput(session, run, 0, batch with { Text = batch.Text + "x" }));
        Assert.ThrowsAny<ArgumentException>(() => store.AppendOutput(session, run, 0, batch with { Text = "\ud800" }));
        Assert.True(store.AppendOutput(session, run, 0, batch));
        Assert.True(store.CompleteRequest(session, run, 0, new(plan.RequestId, ExecutionOutcome.Failed, "actual error")));
        Assert.False(store.CompleteRequest(session, run, 0, new(plan.RequestId, ExecutionOutcome.Failed, "different")));
        Assert.False(store.AppendOutput(session, run, 0, batch with { ChunkIndex = 1 }));
        Assert.Equal(ExecutionOutcome.Failed, store.TrySettle(session, run, ExecutionOutcome.Completed)!.Outcome);
        Assert.Equal("actual error", database.Scalar("SELECT error FROM execution_requests;"));
        Assert.Equal(16384L, database.Scalar("SELECT length(text_utf8) FROM execution_output;"));
    }
}
