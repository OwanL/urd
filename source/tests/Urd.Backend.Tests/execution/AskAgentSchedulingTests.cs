using System.Text.Json;
using Microsoft.Extensions.AI;
using Urd.Backend.Tests.Support;
using Urd.Core.Execution;
using Urd.Core.Sessions;
using Urd.Core.Storage;
using Urd.Core.Tools;
using Xunit;
using static Urd.Backend.Tests.Support.ScriptedResponses;
using static Urd.Backend.Tests.Support.TestAsync;

namespace Urd.Backend.Tests.Execution;

// The ask's effective background mode is decided before the durable claim: wake false forces
// background scheduling, the generic background option is honoured, and the decided mode
// reaches the harness tool context.
public sealed class AskAgentSchedulingTests {
  [Fact]
  public async Task WakeFalseForcesBackgroundBeforeTheDurableClaim() {
    ScheduleRun run = await RunSessionAsync(
    [
      new FunctionCallContent("probe", "probe", new Dictionary<string, object?> { ["background"] = true }),
      new FunctionCallContent("ask", "ask_agent", new Dictionary<string, object?> {
        ["recipient"] = "other", ["prompt"] = "why?", ["wake"] = false
      })
    ]);

    Assert.True(run.Backgrounds["ask_agent"]);
    Assert.True(run.Questions.Background);
    Assert.True(run.Probe.Context is { } context && context.Background);
    Assert.False(run.Questions.Wake);
    Assert.Equal("other", run.Questions.Recipient);
    Assert.Equal("why?", run.Questions.Prompt);
    Assert.Contains("the answer", run.AskResultJson, StringComparison.Ordinal);
  }

  [Fact]
  public async Task AnAwakeAskWithoutBackgroundStaysForeground() {
    ScheduleRun run = await RunSessionAsync(
    [
      new FunctionCallContent("ask", "ask_agent", new Dictionary<string, object?> {
        ["recipient"] = "other", ["prompt"] = "why?"
      })
    ]);

    Assert.False(run.Backgrounds["ask_agent"]);
    Assert.False(run.Questions.Background);
    Assert.True(run.Questions.Wake);
  }

  [Fact]
  public async Task GenericBackgroundIsHonouredForAnAwakeAsk() {
    ScheduleRun run = await RunSessionAsync(
    [
      new FunctionCallContent("ask", "ask_agent", new Dictionary<string, object?> {
        ["recipient"] = "other", ["prompt"] = "why?", ["background"] = true
      })
    ]);

    Assert.True(run.Backgrounds["ask_agent"]);
    Assert.True(run.Questions.Background);
    Assert.True(run.Questions.Wake);
  }

  private static async Task<ScheduleRun> RunSessionAsync(FunctionCallContent[] calls) {
    using var database = new TemporaryDatabase();
    using var store = UrdStore.Open(database.Path);
    SessionRecord record = store.Sessions.CreatePrimary(Guid.NewGuid());
    var probe = new ModeProbeTool();
    var questions = new RecordingQuestions();
    int conversations = 0;
    using var client = new ScriptedChatClient((request, token) => request.Purpose switch {
      "conversation" => Interlocked.Increment(ref conversations) == 1
          ? ToolBatch(token, calls)
          : Complete(token),
      { } pruning when pruning.StartsWith("pruning.", StringComparison.Ordinal) =>
          TextUpdates([PrepassTolerantChatClient.KeepAll(request.Messages)], token),
      "title" => TextUpdates(["probe session"], token),
      _ => throw new InvalidOperationException($"Unexpected purpose '{request.Purpose}'.")
    });
    IChatClient executionClient = PrepassTolerantChatClient.WrapIfNeeded(client, disposeInner: false);
    var session = new PrimarySession(record, store, _ => executionClient, new ToolCatalog([probe, new AskAgentTool()]),
        subagents: questions);
    try {
      UserMessageReceipt receipt = await session.SubmitUserMessageAsync(
          new UserMessageSubmission(Guid.NewGuid(), 0, "go", TestLlmSelection.Default));
      Guid executionId = Assert.IsType<Guid>(receipt.ExecutionId);
      Assert.Equal(ExecutionOutcome.Completed,
          (await session.WaitForExecutionFinishAsync(executionId).WaitAsync(TestTimeout)).Outcome);
      Dictionary<string, bool> backgrounds = database.ReadRows(
          "SELECT name, background FROM execution_tools;",
          row => (row.GetString(0), row.GetInt64(1) != 0))
          .ToDictionary(pair => pair.Item1, pair => pair.Item2);
      string askResultJson = StoredTools.Read(database).Single(call => call.Name == "ask_agent").ResultJson ?? "";
      return new(probe, questions, backgrounds, askResultJson);
    } finally {
      await session.StopAcceptingUserMessagesAsync();
      await session.StopExecutionAsync().WaitAsync(TestTimeout);
    }
  }

  // Everything a scheduling assertion needs after the run settles and the database closes.
  private sealed record ScheduleRun(ModeProbeTool Probe, RecordingQuestions Questions,
      Dictionary<string, bool> Backgrounds, string AskResultJson);

  // A stand-in harness tool that records the calling context, including the decided mode.
  private sealed class ModeProbeTool : HarnessTool {
    public override string Name => "probe";
    public override ToolRelevance RelevantWhen => ToolRelevance.Always;
    public override string Description => "Probe the harness context.";
    public override JsonElement ParametersSchema { get; } = JsonSerializer.SerializeToElement(new { type = "object" });

    internal HarnessToolContext? Context { get; private set; }

    public override Task<ToolOutput> RunAsync(JsonElement arguments, HarnessToolContext context, CancellationToken cancellation) {
      Context = context;
      return Task.FromResult(new ToolOutput("probed", []));
    }
  }

  // Answers every ask immediately and records the effective mode supplied by its caller context.
  private sealed class RecordingQuestions : ISubagentRunner, IAgentQuestions {
    public Task<IReadOnlyList<LiveAgent>> ListLiveAgentsAsync(CancellationToken cancellation) =>
        throw new NotSupportedException();
    public Task<SubagentRunResult> RunSubagentAsync(HarnessToolContext caller, SubagentRunRequest request,
        CancellationToken cancellation) => throw new NotSupportedException();

    internal string? Recipient { get; private set; }
    internal string? Prompt { get; private set; }
    internal bool Wake { get; private set; }
    internal bool Background { get; private set; }

    public Task<AgentQuestionResult> AskAgentAsync(HarnessToolContext caller, string recipient, string prompt,
        bool wake, CancellationToken cancellation) {
      Recipient = recipient;
      Prompt = prompt;
      Wake = wake;
      Background = caller.Background;
      return Task.FromResult(new AgentQuestionResult("the answer", null));
    }

    public bool AnswerAgent(HarnessToolContext caller, string answer) => true;
  }
}
