using System.Text.Json;
using Microsoft.Extensions.AI;
using Urd.Backend.Tests.Support;
using Urd.Core.Compaction;
using Urd.Core.Execution;
using Urd.Core.Providers;
using Urd.Core.Sessions;
using Urd.Core.Storage;
using Xunit;
using static Urd.Backend.Tests.Support.ProviderModelStreams;
using static Urd.Backend.Tests.Support.TestAsync;

namespace Urd.Backend.Tests.Compaction;

// Offline ChatGPT-route compaction: real provider adapter and sign-in transport, every wire
// terminating in scripted fixtures. No live provider traffic.
public sealed class ChatGptCompactionTests {
  private const string Summary = "Goal: retain the latest decision.";
  private const string PromptMarker = "Summarize the removed conversation history";

  [Fact]
  public async Task ManualCompactionKeepsRecentRepliesUsableOnAnotherModel() {
    await using var fixture = new ChatGptFixture();
    PrimarySession session = await fixture.OpenAsync();
    await SubmitTurnAsync(session, fixture.Selection, "ancient-turn: " + new string('x', 100_000));
    await SubmitTurnAsync(session, fixture.Selection, "latest-turn: keep this decision");
    long revision = (await session.ReadSnapshotAsync()).HistoryRevision;

    CompactionReceipt receipt = Assert.IsType<CompactionReceipt>(await session.CompactAsync(
        new ManualCompactionRequest(Guid.NewGuid(), revision, fixture.Selection)));
    Assert.Equal(ExecutionOutcome.Completed,
        (await session.WaitForExecutionFinishAsync(receipt.ExecutionId).WaitAsync(TestTimeout)).Outcome);

    List<ScriptedOpenAIRequest> requests = fixture.Inference.Requests;
    Assert.Equal(3, requests.Count); // Two turns plus one manual summary; no automatic compaction.
    Assert.All(requests, request => Assert.DoesNotContain("max_output_tokens", request.Body, StringComparison.Ordinal));
    Assert.Same(requests[^1], Assert.Single(requests, request => request.Body.Contains(PromptMarker, StringComparison.Ordinal)));

    SavedHistoryCompaction boundary = Assert.IsType<SavedHistoryCompaction>(
        fixture.Store.Compactions.ReadContext(session.Id, revision).PreviousCompaction);
    CompactionSummary saved = Assert.IsType<CompactionSummary>(await session.ReadCompactionAsync(boundary.Id, revision));
    Assert.Equal(Summary, saved.Summary);
    TranscriptHistoryPage history = await session.Transcript.ReadHistoryAsync(new(TranscriptHistoryMode.Latest, revision));
    Assert.Single(history.Entries, entry => entry.Entry.Kind == TranscriptEntryKind.Compaction);

    await SubmitTurnAsync(session, fixture.Selection with { ModelId = "another-model" }, "continue");
    using var switched = JsonDocument.Parse(requests[^1].Body);
    Assert.Equal("another-model", switched.RootElement.GetProperty("model").GetString());
    JsonElement reply = Assert.Single(switched.RootElement.GetProperty("input").EnumerateArray(),
        item => item.TryGetProperty("phase", out _));
    Assert.Equal("final_answer", reply.GetProperty("phase").GetString());
    Assert.False(reply.TryGetProperty("id", out _));
  }

  private static async Task SubmitTurnAsync(PrimarySession session, LlmSelection selection, string text) {
    UserMessageReceipt receipt = await session.SubmitUserMessageAsync(new(Guid.NewGuid(), 0, text, selection));
    Assert.Equal(MessageAcceptance.Accepted, receipt.Acceptance);
    Assert.Equal(ExecutionOutcome.Completed,
        (await session.WaitForExecutionFinishAsync(Assert.IsType<Guid>(receipt.ExecutionId)).WaitAsync(TestTimeout)).Outcome);
  }

  // Full manual compaction over ChatGPT: real sign-in grant and chat client stack, scripted wires.
  // The scripted turns are below the default threshold, so only manual compaction runs.
  private sealed class ChatGptFixture : IAsyncDisposable {
    private readonly TemporaryDatabase database = new();
    private readonly FakeOpenAIIdentity identity = new();
    private readonly UrdStore store;
    private readonly ChatGptProviderTransport transport;
    private readonly ProviderManager manager;
    private IChatClient? client;
    private PrimarySession? session;

    internal ChatGptFixture() {
      store = UrdStore.Open(database.Path);
      ScriptedOpenAIHandler catalog = new("Unexpected catalog request");
      Inference = new(SummaryWire());
      transport = new(identity, catalog, Inference);
      manager = new(store, () => FakeOpenAIIdentity.Callback, transport, FakeOpenAIIdentity.Issuer);
    }

    private static string SummaryWire() {
      string phased = Message.Replace("\"role\":\"assistant\"", "\"role\":\"assistant\",\"phase\":\"final_answer\"", StringComparison.Ordinal);
      return Responses([phased.Replace("hello", Summary, StringComparison.Ordinal)], text: Summary);
    }

    internal UrdStore Store => store;
    internal ScriptedOpenAIHandler Inference { get; }
    internal LlmSelection Selection { get; private set; } = null!;

    internal async Task<PrimarySession> OpenAsync() {
      ProviderConnection connection = await manager.AddChatGptAsync("Synthetic ChatGpt account");
      Selection = new(connection, "gpt-5.4", null, ModelFamily.Other, false);
      ProviderSignIn signIn = await manager.BeginSignInAsync(connection.Id);
      Assert.True(signIn.Status == ProviderSignInStatus.Pending, $"Sign-in status {signIn.Status}: {signIn.Error}");
      Assert.NotNull(signIn.AuthorizationUrl);
      Uri callback = new(await identity.AuthorizeAsync(signIn.AuthorizationUrl));
      string state = FakeOpenAIIdentity.Parse(callback.Query)["state"];
      await manager.CompleteSignInAsync(state, callback.AbsoluteUri).WaitAsync(TestTimeout);
      client = manager.CreateChatClient(connection);
      session = new(store.Sessions.CreatePrimary(Guid.NewGuid()), store, _ => client);
      return session;
    }

    public async ValueTask DisposeAsync() {
      if (session is not null) {
        await session.StopAcceptingUserMessagesAsync();
        await session.StopExecutionAsync().WaitAsync(TestTimeout);
      }
      await manager.ShutdownAsync().WaitAsync(TestTimeout);
      client?.Dispose();
      manager.Dispose();
      store.Dispose();
      database.Dispose();
    }
  }
}
