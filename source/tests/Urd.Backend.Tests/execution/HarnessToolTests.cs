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

public sealed class HarnessToolTests {
  [Fact]
  public async Task ToolResolvesCallingSessionAndParent() {
    using var database = new TemporaryDatabase();
    using var store = UrdStore.Open(database.Path);
    SessionRecord parent = store.Sessions.CreatePrimary(Guid.NewGuid());
    SessionRecord child = store.Sessions.CreateSubagent(Guid.NewGuid(), parent.Id, "owner");
    var tool = new InspectTool();
    int requests = 0;
    using var client = new ScriptedChatClient(token => Interlocked.Increment(ref requests) == 1
        ? ToolBatch(token, new FunctionCallContent("call", tool.Name, new Dictionary<string, object?>()))
        : Complete(token));
    IChatClient executionClient = PrepassTolerantChatClient.WrapIfNeeded(client, disposeInner: false);
    var session = new PrimarySession(child, store, _ => executionClient, new ToolCatalog([tool]));
    try {
      UserMessageReceipt receipt = await session.SubmitUserMessageAsync(
          new UserMessageSubmission(Guid.NewGuid(), 0, "go", TestLlmSelection.Default));
      Guid executionId = Assert.IsType<Guid>(receipt.ExecutionId);
      Assert.Equal(ExecutionOutcome.Completed,
          (await session.WaitForExecutionFinishAsync(executionId).WaitAsync(TestTimeout)).Outcome);
      HarnessToolContext context = Assert.IsType<HarnessToolContext>(tool.Context);
      Assert.Equal(child.Id, context.SessionId);
      Assert.Equal(executionId, context.ExecutionId);
      Assert.Equal(Assert.Single(StoredTools.Read(database)).RequestId, context.RequestId);
      Assert.Equal("call", context.CallId);
      Assert.Equal(child, tool.Caller);
      Assert.Equal(parent, tool.Parent);
    } finally {
      await session.StopAcceptingUserMessagesAsync();
      await session.StopExecutionAsync().WaitAsync(TestTimeout);
    }
  }

  private sealed class InspectTool : HarnessTool {
    public override string Name => "inspect_session";
    public override string Description => "Inspect the calling session.";
    public override ToolRelevance RelevantWhen => ToolRelevance.Always;
    public override JsonElement ParametersSchema { get; } = JsonSerializer.SerializeToElement(new { type = "object" });
    internal HarnessToolContext? Context { get; private set; }
    internal SessionRecord? Caller { get; private set; }
    internal SessionRecord? Parent { get; private set; }

    public override Task<ToolOutput> RunAsync(JsonElement arguments, HarnessToolContext context, CancellationToken cancellation) {
      Context = context;
      Caller = context.Sessions.Read(context.SessionId);
      Parent = Caller?.ParentSessionId is { } id ? context.Sessions.Read(id) : null;
      return Task.FromResult(new ToolOutput("ok", []));
    }
  }
}
