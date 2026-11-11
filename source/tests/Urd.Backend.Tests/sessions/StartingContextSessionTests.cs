using Microsoft.Extensions.AI;
using Urd.Backend.Tests.Support;
using Urd.Core.Context;
using Urd.Core.Execution;
using Urd.Core.Sessions;
using Urd.Core.Storage;
using Urd.Core.Tools;
using Xunit;
using static Urd.Backend.Tests.Support.ScriptedResponses;
using static Urd.Backend.Tests.Support.TestAsync;

namespace Urd.Backend.Tests.Sessions;

public sealed class StartingContextSessionTests {
  [Fact]
  public async Task FirstRequestSavesContextAndReusesItAcrossToolLoopAndNextInput() {
    using var database = new TemporaryDatabase();
    string workingDirectory = Path.Combine(Path.GetTempPath(), "urd-starting-context-original");
    var tool = CreateTool("work");
    Guid sessionId = Guid.Empty;
    UrdStore? requestStore = null;
    int responses = 0;
    using var client = new ScriptedChatClient(token => {
      Assert.NotNull(requestStore!.StartingContexts.Read(sessionId));
      return Interlocked.Increment(ref responses) == 1
          ? ToolBatch(token, new FunctionCallContent("call", "work", new Dictionary<string, object?>()))
          : Complete(token);
    });

    using (UrdStore store = UrdStore.Open(database.Path)) {
      var manager = new SessionManager(store, _ => client, [tool], workingDirectory);
      sessionId = (await manager.CreatePrimaryAsync()).Id;
      Assert.Null(store.StartingContexts.Read(sessionId));
      Assert.Equal(0, client.Calls);
      await manager.ShutdownSessionsAsync().WaitAsync(TestTimeout);
    }

    using UrdStore reopened = UrdStore.Open(database.Path);
    requestStore = reopened;
    Assert.Null(reopened.StartingContexts.Read(sessionId));
    var reopenedManager = new SessionManager(reopened, _ => client, [tool], workingDirectory);
    try {
      PrimarySession session = await reopenedManager.GetPrimaryAsync(sessionId);
      Assert.Null(reopened.StartingContexts.Read(sessionId));
      Assert.Equal(0, client.Calls);
      await SubmitAndWaitAsync(session, "first input");
      await SubmitAndWaitAsync(session, "second input");

      StartingContext saved = Assert.IsType<StartingContext>(reopened.StartingContexts.Read(sessionId));
      Assert.Equal(new[] { "work", "list_pending_calls", "cancel_pending_calls" }, saved.Tools.Select(item => item.Name));
      IReadOnlyList<IReadOnlyList<ChatMessage>> requests = client.Requests;
      Assert.Equal(3, requests.Count);
      for (int index = 0; index < requests.Count; index++) {
        AssertInstructionPrefix(saved.Instructions, requests[index]);
        AssertSameTools(saved.Tools, client.Options[index]);
        Assert.Equal(1, requests[index].Count(message => message.Role == ChatRole.User && message.Text == "first input"));
        Assert.Equal(index == 2 ? 1 : 0,
            requests[index].Count(message => message.Role == ChatRole.User && message.Text == "second input"));
      }
      Assert.Equal(3, client.Calls);
    } finally {
      await reopenedManager.ShutdownSessionsAsync().WaitAsync(TestTimeout);
    }
  }

  [Fact]
  public async Task ReopenUsesSavedContextInsteadOfChangedDirectoryOrToolCatalog() {
    using var database = new TemporaryDatabase();
    string originalDirectory = Path.Combine(Path.GetTempPath(), "urd-starting-context-original");
    string changedDirectory = Path.Combine(Path.GetTempPath(), "urd-starting-context-changed");
    var originalTool = CreateTool("original");
    Guid sessionId = Guid.Empty;
    StartingContext saved = null!;
    using var firstClient = new ScriptedChatClient(Complete);

    using (UrdStore store = UrdStore.Open(database.Path)) {
      var manager = new SessionManager(store, _ => firstClient, [originalTool], originalDirectory);
      try {
        PrimarySession session = await manager.CreatePrimaryAsync();
        sessionId = session.Id;
        await SubmitAndWaitAsync(session, "before reopen");
        saved = Assert.IsType<StartingContext>(store.StartingContexts.Read(sessionId));
        AssertSameTools(saved.Tools, firstClient.Options[0]);
      } finally {
        await manager.ShutdownSessionsAsync().WaitAsync(TestTimeout);
      }
    }

    using UrdStore reopened = UrdStore.Open(database.Path);
    int responses = 0;
    int effects = 0;
    using var reopenedClient = new ScriptedChatClient(token => Interlocked.Increment(ref responses) == 1
        ? ToolBatch(token, new FunctionCallContent("stale", "original", new Dictionary<string, object?>()))
        : Complete(token));
    var changedTool = CreateTool("original", "Changed meaning", () => Interlocked.Increment(ref effects));
    var reopenedManager = new SessionManager(reopened, _ => reopenedClient,
        [changedTool, CreateTool("added")], changedDirectory);
    try {
      PrimarySession session = await reopenedManager.GetPrimaryAsync(sessionId);
      Assert.Equal(Path.GetFullPath(originalDirectory), session.WorkingDirectory);
      Assert.Equal(0, reopenedClient.Calls);
      await SubmitAndWaitAsync(session, "after reopen");

      Assert.Equal(2, reopenedClient.Requests.Count);
      IReadOnlyList<ChatMessage> request = reopenedClient.Requests[0];
      AssertInstructionPrefix(saved.Instructions, request);
      AssertSameTools(saved.Tools, reopenedClient.Options[0]);
      Assert.DoesNotContain(reopenedClient.Options[0]!.Tools!, item => item.Name == "added");
      Assert.Equal(1, request.Count(message => message.Role == ChatRole.User && message.Text == "before reopen"));
      Assert.Equal(1, request.Count(message => message.Role == ChatRole.User && message.Text == "after reopen"));
      Assert.Equal(0, effects);
      ChatMessage result = Assert.Single(reopenedClient.LastRequest, message => message.Role == ChatRole.Tool);
      Assert.Contains("no longer matches", Assert.Single(result.Contents.OfType<FunctionResultContent>()).Result!.ToString());
      Assert.Equal(2, reopenedClient.Calls);
    } finally {
      await reopenedManager.ShutdownSessionsAsync().WaitAsync(TestTimeout);
    }
  }

  private static TestAgentTool CreateTool(string name, string description = "Original meaning", Action? effect = null) =>
      new(AIFunctionFactory.Create(() => {
        effect?.Invoke();
        return "done";
      }, name: name, description: description));

  private static async Task SubmitAndWaitAsync(PrimarySession session, string text) {
    UserMessageReceipt receipt = await session.SubmitUserMessageAsync(
        new UserMessageSubmission(Guid.NewGuid(), 0, text, TestLlmSelection.Default));
    Assert.Equal(MessageAcceptance.Accepted, receipt.Acceptance);
    Assert.Equal(ExecutionOutcome.Completed,
        (await session.WaitForSettlementAsync(Assert.IsType<Guid>(receipt.ExecutionId)).WaitAsync(TestTimeout)).Outcome);
  }

  private static void AssertInstructionPrefix(IReadOnlyList<ChatMessage> instructions, IReadOnlyList<ChatMessage> request) {
    Assert.True(request.Count >= instructions.Count);
    for (int index = 0; index < instructions.Count; index++) {
      Assert.Equal(instructions[index].Role, request[index].Role);
      Assert.Equal(instructions[index].Text, request[index].Text);
    }
  }

  private static void AssertSameTools(IReadOnlyList<AIFunctionDeclaration> expected, ChatOptions? options) {
    AIFunctionDeclaration[] actual = options!.Tools!
        .Select(tool => Assert.IsAssignableFrom<AIFunctionDeclaration>(tool)).ToArray();
    Assert.Equal(expected.Select(tool => tool.Name), actual.Select(tool => tool.Name));
    Assert.Equal(expected.Select(tool => tool.Description), actual.Select(tool => tool.Description));
    Assert.Equal(expected.Select(tool => tool.JsonSchema.GetRawText()), actual.Select(tool => tool.JsonSchema.GetRawText()));
  }
}
