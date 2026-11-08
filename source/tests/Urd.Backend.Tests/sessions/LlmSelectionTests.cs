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
  public async Task EveryOriginalSelectionMemberConflictsButOnlyRenameCanSteer() {
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
      selection with { ReasoningEffort = "high" },
      selection with { Connection = connection with { Id = Guid.NewGuid() } },
      selection with { Connection = connection with { Name = "renamed" } },
      selection with { Connection = connection with { Route = ProviderRoute.OpenAI } },
      selection with { Connection = connection with { Api = LlmApi.OpenAIChatCompletions } },
      selection with { Connection = connection with { Endpoint = new Uri("https://changed.invalid/v1/") } },
      selection with { Connection = connection with { Authentication = AuthenticationMethod.ApiKey } }
    ];
    foreach (LlmSelection choice in changed) {
      await Assert.ThrowsAsync<UserMessageConflictException>(() => session.SubmitUserMessageAsync(original with { LlmSelection = choice }));
      var steering = original with { SubmissionId = Guid.NewGuid(), LlmSelection = choice };
      var decision = store.UserMessages.Admit(session.Id, 0, steering, true, Guid.NewGuid(), execution);
      Assert.Equal(choice.Connection.Name == "renamed" ? MessageAcceptance.Accepted : MessageAcceptance.Refused,
          decision.Receipt.Acceptance);
      Assert.Equal(choice.Connection.Name == "renamed" ? null : MessageRefusalReason.SelectionLocked,
          decision.Receipt.RefusalReason);
      Assert.Equal(decision.Receipt, await session.SubmitUserMessageAsync(steering));
      Assert.Equal(choice, decision.Receipt.Submission.LlmSelection);
    }
    int remaining = 8 - StoredDeliveries.Read(database).Count;
    for (int index = 0; index < remaining; index++) {
      var admitted = store.UserMessages.Admit(session.Id, 0,
          original with { SubmissionId = Guid.NewGuid() }, true, Guid.NewGuid(), execution);
      Assert.True(admitted.NewlyAccepted);
    }
    Assert.Equal(8, StoredDeliveries.Read(database).Count);
    Assert.Equal(accepted, await session.SubmitUserMessageAsync(original));
    Assert.Equal(MessageRefusalReason.Capacity, store.UserMessages.Admit(session.Id, 0,
        original with { SubmissionId = Guid.NewGuid() }, true, Guid.NewGuid(), execution).Receipt.RefusalReason);
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
  public async Task FactoryFailureSettlesAcceptedRequestAndAllowsANewExecution() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    using var client = new ScriptedChatClient(Complete);
    int factories = 0;
    var manager = new SessionManager(store, _ => ++factories == 1
        ? throw new InvalidOperationException("synthetic factory failure") : client);
    try {
      PrimarySession session = await manager.CreatePrimaryAsync();
      var original = new UserMessageSubmission(Guid.NewGuid(), 0, "start", TestLlmSelection.Default);
      UserMessageReceipt accepted = await session.SubmitUserMessageAsync(original);
      Assert.Equal(MessageAcceptance.Accepted, accepted.Acceptance);
      Assert.Equal(ExecutionOutcome.Failed,
          (await session.WaitForSettlementAsync(accepted.ExecutionId!.Value).WaitAsync(TestTimeout)).Outcome);
      Assert.False((await session.ReadSnapshotAsync()).AdmissionFenced);
      Assert.Equal(accepted, await session.SubmitUserMessageAsync(original));
      Assert.Equal(1, factories);
      UserMessageReceipt replacement = await session.SubmitUserMessageAsync(original with { SubmissionId = Guid.NewGuid() });
      Assert.Equal(ExecutionOutcome.Completed,
          (await session.WaitForSettlementAsync(replacement.ExecutionId!.Value).WaitAsync(TestTimeout)).Outcome);
      Assert.Equal(2, factories);
      Assert.Equal(1, client.Calls);
    } finally {
      await manager.ShutdownSessionsAsync().WaitAsync(TestTimeout);
    }
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

  [Fact]
  public void LatestSelectionOutlivesItsInitiatingPreviewAndChangesOnlyWithANewExecution() {
    using var database = new TemporaryDatabase();
    Guid session = Guid.NewGuid(), execution = Guid.NewGuid();
    LlmSelection selected = TestLlmSelection.Default with { ReasoningEffort = "low" };
    using (UrdStore store = UrdStore.Open(database.Path)) {
      store.Sessions.CreatePrimary(session);
      Assert.Null(store.Snapshots.ReadStored(session).LatestLlmSelection);
      for (int index = 0; index < 65; index++) {
        var submission = new UserMessageSubmission(Guid.NewGuid(), 0, $"input {index}", selected);
        store.UserMessages.Admit(session, 0, submission, true, execution, execution);
        LlmRequest request = store.Executions.ClaimNextRequest(session, execution, 0)!;
        Assert.True(store.LlmResponses.Append(session, execution, 0, new(request.RequestId, Guid.NewGuid(), 0, "", true)));
        Assert.True(store.LlmResponses.CompleteRequest(session, execution, 0, new(request.RequestId, ExecutionOutcome.Completed)));
      }
      SessionSnapshot snapshot = store.Snapshots.ReadStored(session);
      Assert.Equal(60, snapshot.Messages.Count);
      Assert.True(snapshot.HasOlderMessages);
      Assert.DoesNotContain(snapshot.Messages, message => message.Preview == "input 0");
      Assert.Equal(selected, snapshot.LatestLlmSelection);
      Assert.NotNull(store.Executions.TrySettle(session, execution, ExecutionOutcome.Completed));
      Assert.Equal(selected, store.Snapshots.ReadStored(session).LatestLlmSelection);
    }
    using UrdStore reopened = UrdStore.Open(database.Path);
    Assert.Equal(selected, reopened.Snapshots.ReadStored(session).LatestLlmSelection);
    var changed = selected with { ModelId = "next-model" };
    reopened.UserMessages.Admit(session, 0, new(Guid.NewGuid(), 0, "next", changed), true, Guid.NewGuid());
    Assert.Equal(changed, reopened.Snapshots.ReadStored(session).LatestLlmSelection);
  }
}
