using Microsoft.Extensions.AI;
using Urd.Backend.Tests.Support;
using Urd.Core.Execution;
using Urd.Core.Providers;
using Urd.Core.Sessions;
using Urd.Core.Storage;
using Xunit;
using static Urd.Backend.Tests.Support.ScriptedResponses;
using static Urd.Backend.Tests.Support.TestAsync;

#pragma warning disable MEAI001

namespace Urd.Backend.Tests.Sessions;

public sealed class LlmSelectionTests {
  [Fact]
  public async Task ChangedSelectionCannotRetryOrSteer() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    using var client = new ScriptedChatClient(Complete);
    var manager = new SessionManager(store, _ => client);
    PrimarySession session = await manager.CreatePrimaryAsync();
    // Admit through storage so the active execution stays held without a provider task.
    Guid execution = Guid.NewGuid();
    var original = new UserMessageSubmission(Guid.NewGuid(), 0, "original", TestLlmSelection.Default);
    UserMessageReceipt accepted = store.UserMessages.Admit(session.Id, 0, original, true, execution).Receipt;
    LlmSelection selection = original.LlmSelection!;
    ProviderConnection connection = selection.Connection;
    LlmSelection[] changed = [
      selection with { ModelId = "another-model" },
      selection with { Connection = connection with { Endpoint = new Uri("https://changed.invalid/v1/") } }
    ];
    foreach (LlmSelection choice in changed) {
      await Assert.ThrowsAsync<UserMessageConflictException>(() => session.SubmitUserMessageAsync(original with { LlmSelection = choice }));
      var steering = original with { SubmissionId = Guid.NewGuid(), LlmSelection = choice };
      var decision = store.UserMessages.Admit(session.Id, 0, steering, true, Guid.NewGuid(), execution);
      Assert.Equal(MessageAcceptance.Refused, decision.Receipt.Acceptance);
      Assert.Equal(MessageRefusalReason.SelectionLocked, decision.Receipt.RefusalReason);
      Assert.Equal(decision.Receipt, await session.SubmitUserMessageAsync(steering));
      Assert.Equal(choice, decision.Receipt.Submission.LlmSelection);
    }
    Assert.Equal(accepted, await session.SubmitUserMessageAsync(original));
    store.Executions.RequestStop(session.Id, execution, true);
    Assert.Equal(accepted, await session.SubmitUserMessageAsync(original));
    Assert.Equal(selection, (await session.ReadSnapshotAsync()).LatestLlmSelection);
    Assert.Equal(0, client.Calls);
    await manager.ShutdownSessionsAsync().WaitAsync(TestTimeout);
  }

  [Fact]
  public async Task LazyBorrowedFactoryCanReenterAndAllToolRequestsKeepTheInitiatingOptions() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    int round = 0, factories = 0;
    using var client = new ScriptedChatClient(token => ++round == 1
        ? ToolBatch(token, new FunctionCallContent("call", "step", new Dictionary<string, object?> { ["number"] = 1 }))
        : Complete(token));
    PrimarySession? session = null;
    LlmSelection selection = TestLlmSelection.Default with { ModelId = "chosen-model", ReasoningEffort = "high" };
    var manager = new SessionManager(store, binding => {
      Interlocked.Increment(ref factories);
      Assert.Equal(selection.Connection, binding);
      Assert.Equal(selection, session!.ReadSnapshotAsync().WaitAsync(TestTimeout).GetAwaiter().GetResult().LatestLlmSelection);
      return client;
    }, [AIFunctionFactory.Create((int number) => number, name: "step")]);
    try {
      session = await manager.CreatePrimaryAsync();
      Assert.Equal(0, factories);
      UserMessageReceipt accepted = await session.SubmitUserMessageAsync(new(Guid.NewGuid(), 0, "start", selection));
      Assert.Equal(ExecutionOutcome.Completed,
          (await session.WaitForSettlementAsync(accepted.ExecutionId!.Value).WaitAsync(TestTimeout)).Outcome);
      Assert.Equal(1, factories);
      Assert.Equal(2, client.Calls);
      Assert.All(client.Options, options => {
        Assert.Equal(selection.ModelId, options!.ModelId);
        Assert.Equal(ReasoningEffort.High, options.Reasoning!.Effort);
      });
    } finally {
      await manager.ShutdownSessionsAsync().WaitAsync(TestTimeout);
    }
    Assert.Equal(0, client.DisposeCalls);
  }

  [Fact]
  public async Task SavedBindingCannotFollowAnEndpointChangeAndReceiptsSurviveProviderRemoval() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    var transport = new ScriptedOpenAIHandler(ProviderModelStreams.Responses([ProviderModelStreams.Message]));
    using var providers = new ProviderManager(store, () => new Uri("http://127.0.0.1:1234/auth/callback"), transport);
    int factories = 0;
    var manager = new SessionManager(store, binding => {
      Interlocked.Increment(ref factories);
      return providers.CreateChatClient(binding);
    });
    Guid sessionId;
    UserMessageReceipt accepted, failed;
    try {
      ProviderConnection connection = await providers.AddCustomAsync(new("original", LlmApi.OpenAIResponses,
          new Uri("https://fixture.invalid/v1/"), AuthenticationMethod.None));
      LlmSelection selection = TestLlmSelection.Create(connection);
      await providers.RenameAsync(connection.Id, "renamed");
      PrimarySession session = await manager.CreatePrimaryAsync();
      sessionId = session.Id;
      accepted = await session.SubmitUserMessageAsync(new(Guid.NewGuid(), 0, "go", selection));
      Assert.Equal(ExecutionOutcome.Completed,
          (await session.WaitForSettlementAsync(accepted.ExecutionId!.Value).WaitAsync(TestTimeout)).Outcome);
      Assert.Equal(accepted, await session.SubmitUserMessageAsync(accepted.Submission));
      await providers.UpdateCustomAsync(connection.Id, new("renamed", connection.Api,
          new Uri("https://changed.invalid/v1/"), connection.Authentication));
      failed = await session.SubmitUserMessageAsync(new(Guid.NewGuid(), 0, "old binding", selection));
      Assert.Equal(MessageAcceptance.Accepted, failed.Acceptance);
      Assert.Equal(ExecutionOutcome.Failed,
          (await session.WaitForSettlementAsync(failed.ExecutionId!.Value).WaitAsync(TestTimeout)).Outcome);
      Assert.Equal(connection.Endpoint.Host, Assert.Single(transport.Requests).Uri.Host);
      await providers.RemoveAsync(connection.Id);
      Assert.Equal(accepted, await session.SubmitUserMessageAsync(accepted.Submission));
      Assert.Equal(failed, await session.SubmitUserMessageAsync(failed.Submission));
      Assert.Equal(selection, (await session.ReadSnapshotAsync()).LatestLlmSelection);
      Assert.Equal(2, factories);
    } finally {
      await manager.ShutdownSessionsAsync().WaitAsync(TestTimeout);
      await providers.ShutdownAsync().WaitAsync(TestTimeout);
    }
    using UrdStore reopened = UrdStore.Open(database.Path);
    var loadedManager = new SessionManager(reopened, _ => throw new InvalidOperationException("Retained receipts must not resolve providers."));
    try {
      PrimarySession loaded = await loadedManager.GetPrimaryAsync(sessionId);
      Assert.Equal(accepted, await loaded.SubmitUserMessageAsync(accepted.Submission));
      Assert.Equal(failed, await loaded.SubmitUserMessageAsync(failed.Submission));
      Assert.Equal(accepted.Submission.LlmSelection, (await loaded.ReadSnapshotAsync()).LatestLlmSelection);
    } finally {
      await loadedManager.ShutdownSessionsAsync().WaitAsync(TestTimeout);
    }
  }

}
