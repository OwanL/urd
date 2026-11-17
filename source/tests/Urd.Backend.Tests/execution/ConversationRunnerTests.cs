using Microsoft.Extensions.AI;
using Urd.Backend.Tests.Support;
using Urd.Core.Execution;
using Urd.Core.Tools;
using Urd.Core.Usage;
using Xunit;
using static Urd.Backend.Tests.Support.ScriptedResponses;
using static Urd.Backend.Tests.Support.TestAsync;

namespace Urd.Backend.Tests.Execution;

// ConversationRunner owns one isolated auxiliary loop over a borrowed client; the parent
// execution stays paused and must settle cleanly after the auxiliary run joins.
public sealed class ConversationRunnerTests {
  [Fact]
  public async Task ToolRoundCompletesDurablyAndProjectsPortableContinuation() {
    var mainWaiting = Signal();
    var mainRelease = Signal();
    var main = new ScriptedChatClient(token => Paused(mainWaiting, mainRelease, token));
    await using var fixture = new ExecutionFixture(main);
    Guid execution = await fixture.StartExecutionAsync("main input");
    await mainWaiting.Task.WaitAsync(TestTimeout);

    AgentTool[] tools = [
      new TestAgentTool("read", (_, _, _) => Task.FromResult(new ToolOutput("explored", []))),
      new TestAgentTool("write", (_, _, _) => throw new InvalidOperationException("Excluded from auxiliary runs.")),
      new TestAgentTool("bash", (_, _, _) => Task.FromResult(new ToolOutput("shelled", [])))
    ];
    int scriptedCalls = 2;
    var client = new ScriptedChatClient(token => Script(token));
    var runner = new ConversationRunner(fixture.Session.Recorder, _ => client, tools, fixture.Session.WorkingDirectory);
    ChatMessage[] input = [new(ChatRole.System, "Choose resources."), new(ChatRole.User, "explore")];

    string answer = await runner.RunAsync("examination", TestLlmSelection.DefaultResolution, input, null,
        CancellationToken.None).WaitAsync(TestTimeout);

    Assert.Equal("done", answer);
    Assert.Equal(2, client.Calls);
    Assert.Equal(0, client.DisposeCalls);
    Assert.Equal("fixture-model", client.Options[0]!.ModelId);
    string[] offered = [.. client.Options[0]!.Tools!.Select(tool => tool.Name)];
    Assert.Equal(["read", "bash"], offered);
    Assert.Equal("examination", Assert.IsType<UsageContext>(
        client.Options[0]!.AdditionalProperties![UsageContext.PropertyName]).Purpose);

    IReadOnlyList<ChatMessage> continuation = client.Requests[1];
    ChatMessage assistant = continuation.Single(message => message.Role == ChatRole.Assistant);
    FunctionCallContent call = Assert.IsType<FunctionCallContent>(Assert.Single(assistant.Contents));
    Assert.Equal("call-1", call.CallId);
    ChatMessage results = continuation.Single(message => message.Role == ChatRole.Tool);
    FunctionResultContent result = Assert.IsType<FunctionResultContent>(Assert.Single(results.Contents));
    Assert.Equal("call-1", result.CallId);
    Assert.Equal("explored", result.Result);

    Assert.Equal(1L, fixture.Database.Scalar(
        "SELECT COUNT(*) FROM auxiliary_conversations WHERE purpose = 'examination';")); // raw-sql: auxiliary rows are the runner's durable scope
    long[] outcomes = [.. fixture.Database.ReadRows(
        "SELECT outcome FROM auxiliary_requests ORDER BY request_index;", row => row.GetInt64(0))];
    Assert.Equal([0L, 0L], outcomes);
    List<(string CallId, string Name, bool Started, long? Outcome, string? ResultJson)> stored = fixture.Database.ReadRows("""
        SELECT call_id, name, started, outcome, result_json FROM auxiliary_tools ORDER BY call_index;
        """, row => (row.GetString(0), row.GetString(1), row.GetInt64(2) != 0,
            row.IsDBNull(3) ? null : (long?)row.GetInt64(3), row.IsDBNull(4) ? null : row.GetString(4)));
    (string CallId, string Name, bool Started, long? Outcome, string? ResultJson) tool = Assert.Single(stored);
    Assert.Equal("call-1", tool.CallId);
    Assert.Equal("read", tool.Name);
    Assert.True(tool.Started);
    Assert.Equal(0L, tool.Outcome);
    Assert.Contains("explored", tool.ResultJson);
    Assert.Equal(1L, fixture.Database.Scalar(
        "SELECT COUNT(*) FROM auxiliary_observed_calls WHERE name = 'read';")); // raw-sql: observations are recorded before dispatch

    // The caller's input is untouched and the main history stays free of auxiliary messages.
    Assert.Equal("explore", input[1].Text);
    Assert.Single(main.Requests);
    Assert.DoesNotContain(main.LastRequest, message => message.Text.Contains("explore", StringComparison.Ordinal));

    try {
      mainRelease.TrySetResult();
      Assert.Equal(ExecutionOutcome.Completed,
          (await fixture.Session.WaitForExecutionFinishAsync(execution).WaitAsync(TestTimeout)).Outcome);
    } finally {
      mainRelease.TrySetResult();
    }

    IAsyncEnumerable<ChatResponseUpdate> Script(CancellationToken token) =>
        --scriptedCalls == 0 ? TextUpdates(["done"], token)
        : ToolBatch(token, new FunctionCallContent("call-1", "read", new Dictionary<string, object?> { ["path"] = "notes.txt" }));
  }

  [Fact]
  public async Task ValidatorAppendsOneCorrectionAndAcceptsCorrectedAnswer() {
    var mainWaiting = Signal();
    var mainRelease = Signal();
    var main = new ScriptedChatClient(token => Paused(mainWaiting, mainRelease, token));
    await using var fixture = new ExecutionFixture(main);
    Guid execution = await fixture.StartExecutionAsync("main input");
    await mainWaiting.Task.WaitAsync(TestTimeout);

    int scriptedCalls = 2;
    var client = new ScriptedChatClient(token => Script(token));
    int evaluations = 0;
    string? Evaluate(string text) {
      Interlocked.Increment(ref evaluations);
      return text == "second" ? null : "The session name must be short.";
    }
    var runner = new ConversationRunner(fixture.Session.Recorder, _ => client, [], fixture.Session.WorkingDirectory);

    string answer = await runner.RunAsync("title", TestLlmSelection.DefaultResolution,
        [new(ChatRole.User, "name this session")], Evaluate, CancellationToken.None).WaitAsync(TestTimeout);

    Assert.Equal("second", answer);
    Assert.Equal(2, client.Calls);
    Assert.Equal(2, evaluations);
    ChatMessage correction = client.Requests[1][^1];
    Assert.Equal(ChatRole.User, correction.Role);
    Assert.Equal("The session name must be short.", correction.Text);
    long[] outcomes = [.. fixture.Database.ReadRows(
        "SELECT outcome FROM auxiliary_requests ORDER BY request_index;", row => row.GetInt64(0))];
    Assert.Equal([0L, 0L], outcomes);

    try {
      mainRelease.TrySetResult();
      await fixture.Session.WaitForExecutionFinishAsync(execution).WaitAsync(TestTimeout);
    } finally {
      mainRelease.TrySetResult();
    }

    IAsyncEnumerable<ChatResponseUpdate> Script(CancellationToken token) =>
        --scriptedCalls == 0 ? TextUpdates(["second"], token) : TextUpdates(["first"], token);
  }

  [Fact]
  public async Task RejectedCorrectedAnswerThrowsOrdinaryInvalidData() {
    var mainWaiting = Signal();
    var mainRelease = Signal();
    var main = new ScriptedChatClient(token => Paused(mainWaiting, mainRelease, token));
    await using var fixture = new ExecutionFixture(main);
    Guid execution = await fixture.StartExecutionAsync("main input");
    await mainWaiting.Task.WaitAsync(TestTimeout);

    int scriptedCalls = 2;
    var client = new ScriptedChatClient(token => Script(token));
    var runner = new ConversationRunner(fixture.Session.Recorder, _ => client, [], fixture.Session.WorkingDirectory);

    await Assert.ThrowsAsync<InvalidDataException>(() => runner.RunAsync("title", TestLlmSelection.DefaultResolution,
        [new(ChatRole.User, "name this session")], _ => "still wrong", CancellationToken.None).WaitAsync(TestTimeout));

    Assert.Equal(2, client.Calls);
    long[] outcomes = [.. fixture.Database.ReadRows(
        "SELECT outcome FROM auxiliary_requests ORDER BY request_index;", row => row.GetInt64(0))];
    Assert.Equal([0L, 0L], outcomes);

    try {
      mainRelease.TrySetResult();
      await fixture.Session.WaitForExecutionFinishAsync(execution).WaitAsync(TestTimeout);
    } finally {
      mainRelease.TrySetResult();
    }

    IAsyncEnumerable<ChatResponseUpdate> Script(CancellationToken token) =>
        --scriptedCalls == 0 ? TextUpdates(["second"], token) : TextUpdates(["first"], token);
  }

  [Fact]
  public async Task RecordingFailureThrowsFatalAuxiliaryException() {
    var mainWaiting = Signal();
    var mainRelease = Signal();
    var main = new ScriptedChatClient(token => Paused(mainWaiting, mainRelease, token));
    await using var fixture = new ExecutionFixture(main) { ExpectFencedExecution = true };
    await fixture.StartExecutionAsync("main input");
    await mainWaiting.Task.WaitAsync(TestTimeout);

    fixture.Database.Execute("""
      CREATE TRIGGER reject_auxiliary_output BEFORE INSERT ON auxiliary_output
      BEGIN SELECT RAISE(ABORT, 'Injected auxiliary output failure'); END;
      """);
    var client = new ScriptedChatClient(token => TextUpdates(["first"], token));
    var runner = new ConversationRunner(fixture.Session.Recorder, _ => client, [], fixture.Session.WorkingDirectory);

    try {
      await Assert.ThrowsAsync<AuxiliaryExecutionException>(() => runner.RunAsync("title", TestLlmSelection.DefaultResolution,
          [new(ChatRole.User, "private title task")], null, CancellationToken.None).WaitAsync(TestTimeout));
      Assert.Equal(1, client.Calls);
      Assert.Equal(0, client.DisposeCalls);
      Assert.Equal(1L, fixture.Database.Scalar("SELECT COUNT(*) FROM auxiliary_requests;")); // raw-sql: the durable request stays unsettled
      Assert.Equal(1L, fixture.Database.Scalar(
          "SELECT COUNT(*) FROM auxiliary_requests WHERE outcome IS NULL;")); // raw-sql: no ordinary failed completion masks the save fault
    } finally {
      mainRelease.TrySetResult();
    }
  }

  [Fact]
  public async Task PrecancelledRunStartsNoConversationOrProviderContact() {
    var mainWaiting = Signal();
    var mainRelease = Signal();
    var main = new ScriptedChatClient(token => Paused(mainWaiting, mainRelease, token));
    await using var fixture = new ExecutionFixture(main);
    Guid execution = await fixture.StartExecutionAsync("main input");
    await mainWaiting.Task.WaitAsync(TestTimeout);

    var client = new ScriptedChatClient(token => TextUpdates(["ignored"], token));
    var runner = new ConversationRunner(fixture.Session.Recorder, _ => client, [], fixture.Session.WorkingDirectory);

    await Assert.ThrowsAsync<OperationCanceledException>(() => runner.RunAsync("title", TestLlmSelection.DefaultResolution,
        [new(ChatRole.User, "private title task")], null, new CancellationToken(canceled: true)).WaitAsync(TestTimeout));

    Assert.Equal(0, client.Calls);
    Assert.Equal(0, client.DisposeCalls);
    Assert.Equal(0L, fixture.Database.Scalar("SELECT COUNT(*) FROM auxiliary_conversations;")); // raw-sql: no work is admitted

    try {
      mainRelease.TrySetResult();
      await fixture.Session.WaitForExecutionFinishAsync(execution).WaitAsync(TestTimeout);
    } finally {
      mainRelease.TrySetResult();
    }
  }
}
