using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Urd.Backend.Tests.Support;
using Urd.Core.Execution;
using Urd.Core.Sessions;
using Urd.Core.Storage;
using Xunit;
using static Urd.Backend.Tests.Support.ScriptedResponses;
using static Urd.Backend.Tests.Support.TestAsync;

namespace Urd.Backend.Tests.Execution;

public sealed class ToolExecutionTests {
  [Fact]
  public async Task OrdinaryToolErrorDoesNotPreventLaterCallsOrLlmContinuation() {
    int invoked = 0;
    AIFunction count = AIFunctionFactory.Create((int amount) => {
      invoked++;
      if (amount == 1)
        throw new InvalidOperationException("Invalid command.");
      return amount;
    }, name: "count");
    int requests = 0;
    var client = new ScriptedChatClient(token => Interlocked.Increment(ref requests) == 1
        ? ToolBatch(token,
            new("first", "count", new Dictionary<string, object?> { ["amount"] = 1 }),
            new("second", "count", new Dictionary<string, object?> { ["amount"] = 2 }))
        : Complete(token));
    await using var fixture = new ExecutionFixture(client, tools: [count]);
    Guid executionId = await fixture.StartExecutionAsync("go");

    Assert.Equal(ExecutionOutcome.Completed,
        (await fixture.Session.WaitForSettlementAsync(executionId).WaitAsync(TestTimeout)).Outcome);
    Assert.Equal(2, invoked);
    Assert.Equal(2, client.Calls);
    Assert.All(client.Options, options => Assert.Same(count, Assert.Single(options!.Tools!)));
    FunctionResultContent[] results = client.LastRequest.SelectMany(message => message.Contents)
        .OfType<FunctionResultContent>().ToArray();
    Assert.Equal(2, results.Length);
    Assert.Equal("first", results[0].CallId);
    Assert.False(string.IsNullOrEmpty(Assert.IsType<JsonElement>(results[0].Result).GetProperty("error").GetString()));
    Assert.Equal("second", results[1].CallId);
    Assert.Equal(2, Assert.IsType<JsonElement>(results[1].Result).GetInt32());
    List<StoredTool> tools = StoredTools.Read(fixture.Database);
    Assert.Single(tools, tool => tool.Outcome == ExecutionOutcome.Failed);
    Assert.Equal(2, tools.Count(tool => tool.ResultJson is not null));
  }

  [Fact]
  public async Task ToolEffectsWaitForSuccessfulProviderCleanup() {
    int effects = 0;
    AIFunction effect = AIFunctionFactory.Create(() => Interlocked.Increment(ref effects), name: "effect");
    var cleanupEntered = Signal();
    var cleanupRelease = Signal();
    int requests = 0;
    var client = new ScriptedChatClient(token => Interlocked.Increment(ref requests) == 1
        ? new HeldDisposal(ToolCall(token), cleanupEntered, cleanupRelease.Task)
        : Complete(token));
    await using var fixture = new ExecutionFixture(client, tools: [effect]);
    Guid executionId = await fixture.StartExecutionAsync("go");
    Task<SessionExecutionResult> settlement = fixture.Session.WaitForSettlementAsync(executionId);

    try {
      await cleanupEntered.Task.WaitAsync(TestTimeout);
      Assert.Equal(0, Volatile.Read(ref effects));
      Assert.False(settlement.IsCompleted);
    } finally {
      cleanupRelease.TrySetResult();
      await settlement.WaitAsync(TestTimeout);
    }

    Assert.Equal(ExecutionOutcome.Completed, (await settlement).Outcome);
    Assert.Equal(1, Volatile.Read(ref effects));
    Assert.Equal(2, client.Calls);
    Assert.Equal("call", Assert.Single(client.LastRequest.SelectMany(message => message.Contents)
        .OfType<FunctionResultContent>()).CallId);

    static async IAsyncEnumerable<ChatResponseUpdate> ToolCall(
        [EnumeratorCancellation] CancellationToken token) {
      await Task.CompletedTask;
      token.ThrowIfCancellationRequested();
      yield return new() {
        Contents = [new FunctionCallContent("call", "effect", null)],
        FinishReason = ChatFinishReason.ToolCalls
      };
    }
  }

  [Fact]
  public async Task InterleavedTextAndCallsPersistInProviderOrderWithGlobalRunCursors() {
    int requests = 0;
    AIFunction effect = AIFunctionFactory.Create(() => "result", name: "effect");
    var client = new ScriptedChatClient(token => Interlocked.Increment(ref requests) == 1
        ? Interleaved(token) : Complete(token));
    await using var fixture = new ExecutionFixture(client, tools: [effect]);
    Guid executionId = await fixture.StartExecutionAsync("go");

    Assert.Equal(ExecutionOutcome.Completed,
        (await fixture.Session.WaitForSettlementAsync(executionId).WaitAsync(TestTimeout)).Outcome);
    StoredEntry[] entries = ReadEntries(fixture.Database);
    Assert.Equal(new[] { 0, 1, 2, 1, 1 }, entries.Select(entry => entry.Role));
    Assert.Equal(new[] { "before", "effect-call", "after" }, entries.Skip(1).Take(3).Select(entry => entry.Label));

    MessageRef before = new(executionId, entries[1].MessageId, MessageRole.Assistant, 0);
    MessageRef after = new(executionId, entries[3].MessageId, MessageRole.Assistant, 0);
    MessageContentPage first = await fixture.Session.ReadMessageContentAsync(new(before, 0, 0));
    MessageContentPage second = await fixture.Session.ReadMessageContentAsync(new(after, 0, 0));
    Assert.Equal("before", first.Text);
    Assert.Equal(new(before, 1, 0), first.AvailableThrough);
    Assert.True(first.IsClosed);
    Assert.Equal("after", second.Text);
    Assert.Equal(new(after, 2, 0), second.AvailableThrough);
    Assert.Equal(1, second.From.SegmentIndex);
    Assert.True(second.FinalOutputCommitted);

    Assert.All(entries, entry => {
      Assert.True(DateTimeOffset.TryParse(entry.CreatedAt, CultureInfo.InvariantCulture,
          DateTimeStyles.RoundtripKind, out DateTimeOffset created));
      Assert.Equal(TimeSpan.Zero, created.Offset);
    });
    Assert.True(Assert.Single(StoredTools.Read(fixture.Database)).Started);
    using (SqliteConnection connection = fixture.Database.OpenReadOnlyConnection())
    using (SqliteCommand command = connection.CreateCommand()) {
      command.CommandText = "SELECT started_at, ended_at FROM execution_tools;";
      using SqliteDataReader reader = await command.ExecuteReaderAsync();
      Assert.True(await reader.ReadAsync());
      Assert.True(DateTimeOffset.TryParse(reader.GetString(0), CultureInfo.InvariantCulture,
          DateTimeStyles.RoundtripKind, out DateTimeOffset startedAt));
      Assert.True(DateTimeOffset.TryParse(reader.GetString(1), CultureInfo.InvariantCulture,
          DateTimeStyles.RoundtripKind, out DateTimeOffset endedAt));
      Assert.Equal(TimeSpan.Zero, startedAt.Offset);
      Assert.Equal(TimeSpan.Zero, endedAt.Offset);
    }

    static async IAsyncEnumerable<ChatResponseUpdate> Interleaved([EnumeratorCancellation] CancellationToken token) {
      await Task.CompletedTask;
      token.ThrowIfCancellationRequested();
      yield return Text("before");
      yield return new() { Contents = [new FunctionCallContent("effect-call", "effect", new Dictionary<string, object?>())] };
      yield return Text("after");
      yield return new() { FinishReason = ChatFinishReason.ToolCalls };
    }
  }

  [Fact]
  public async Task CompletionOnlyCallsAreAppendedAfterText() {
    int requests = 0;
    AIFunction effect = AIFunctionFactory.Create(() => "result", name: "effect");
    var client = new ScriptedChatClient(token => Interlocked.Increment(ref requests) == 1
        ? TextThenCall(token) : Complete(token));
    await using var fixture = new ExecutionFixture(client, tools: [effect]);
    Guid executionId = await fixture.StartExecutionAsync("go");

    Assert.Equal(ExecutionOutcome.Completed,
        (await fixture.Session.WaitForSettlementAsync(executionId).WaitAsync(TestTimeout)).Outcome);
    Assert.Equal(new[] { 0, 1, 2, 1 }, ReadEntries(fixture.Database).Select(entry => entry.Role));
    Assert.Equal(new[] { "visible", "effect-call" }, ReadEntries(fixture.Database).Skip(1).Take(2).Select(entry => entry.Label));

    static async IAsyncEnumerable<ChatResponseUpdate> TextThenCall([EnumeratorCancellation] CancellationToken token) {
      await Task.CompletedTask;
      token.ThrowIfCancellationRequested();
      yield return Text("visible");
      yield return new() {
        Contents = [new FunctionCallContent("effect-call", "effect", new Dictionary<string, object?>())],
        FinishReason = ChatFinishReason.ToolCalls
      };
    }
  }

  [Fact]
  public async Task FailedObservedCallIsNeverAuthorizedOrStarted() {
    int effects = 0;
    AIFunction effect = AIFunctionFactory.Create(() => ++effects, name: "effect");
    var client = new ScriptedChatClient(FailAfterCall);
    await using var fixture = new ExecutionFixture(client, tools: [effect]);
    Guid executionId = await fixture.StartExecutionAsync("go");

    Assert.Equal(ExecutionOutcome.Failed,
        (await fixture.Session.WaitForSettlementAsync(executionId).WaitAsync(TestTimeout)).Outcome);
    Assert.Equal(0, effects);
    Assert.Empty(StoredTools.Read(fixture.Database));
    StoredEntry reserved = Assert.Single(ReadEntries(fixture.Database), entry => entry.Role == 2);
    Assert.Equal("effect-call", reserved.Label);
    Assert.NotNull(reserved.CreatedAt);
    Assert.Equal((long)ExecutionOutcome.Failed, fixture.Database.Scalar(
        "SELECT outcome FROM execution_requests WHERE request_id = (SELECT request_id FROM execution_messages WHERE role = 2);"));

    static async IAsyncEnumerable<ChatResponseUpdate> FailAfterCall([EnumeratorCancellation] CancellationToken token) {
      await Task.CompletedTask;
      token.ThrowIfCancellationRequested();
      yield return Text("working");
      yield return new() { Contents = [new FunctionCallContent("effect-call", "effect", new Dictionary<string, object?>())] };
      yield return new() { FinishReason = ChatFinishReason.Length };
    }
  }

  [Fact]
  public async Task PayloadsLargerThanSixteenKiBPersistReconstructAndReadInBoundedPages() {
    string userText = new('u', 20_000);
    string assistantText = new('a', 35_000);
    string largeArgument = new('x', 20_000);
    string largeResult = new('r', 21_000);
    int requests = 0;
    AIFunction large = AIFunctionFactory.Create((string payload) => largeResult, name: "large");
    var client = new ScriptedChatClient(token => Interlocked.Increment(ref requests) == 1
        ? LargeToolRequest(token, assistantText, largeArgument) : Complete(token));
    await using var fixture = new ExecutionFixture(client, tools: [large]);
    Guid executionId = await fixture.StartExecutionAsync(userText);

    Assert.Equal(ExecutionOutcome.Completed,
        (await fixture.Session.WaitForSettlementAsync(executionId).WaitAsync(TestTimeout)).Outcome);
    Assert.Equal(userText, Assert.Single(client.Requests[0], message => message.Role == ChatRole.User).Text);
    ChatMessage[] continuation = client.Requests[1].ToArray();
    Assert.Equal(assistantText, Assert.Single(continuation.Where(message => message.Role == ChatRole.Assistant)
        .SelectMany(message => message.Contents).OfType<TextContent>()).Text);
    FunctionCallContent savedCall = Assert.Single(continuation.SelectMany(message => message.Contents).OfType<FunctionCallContent>());
    Assert.Equal(largeArgument, Assert.IsType<JsonElement>(savedCall.Arguments!["payload"]).GetString());
    FunctionResultContent savedResult = Assert.Single(continuation.SelectMany(message => message.Contents).OfType<FunctionResultContent>());
    Assert.Equal(largeResult, Assert.IsType<JsonElement>(savedResult.Result).GetString());

    StoredTool tool = Assert.Single(StoredTools.Read(fixture.Database));
    Assert.True(System.Text.Encoding.UTF8.GetByteCount(tool.ArgumentsJson) > 16 * 1024);
    Assert.True(System.Text.Encoding.UTF8.GetByteCount(tool.ResultJson!) > 16 * 1024);

    Guid submissionId = Assert.Single(StoredExecutions.Read(fixture.Database)).SubmissionId;
    var userRef = new MessageRef(executionId, submissionId, MessageRole.User, 0);
    MessageContentPage userPage = await fixture.Session.ReadMessageContentAsync(new(userRef, 0, 0));
    Assert.Equal(16 * 1024, Encoding.UTF8.GetByteCount(userPage.Text));
    Assert.True(userPage.HasMore);
    MessageContentPage userRemainder = await fixture.Session.ReadMessageContentAsync(userPage.Next);
    Assert.Equal(userText, userPage.Text + userRemainder.Text);

    StoredEntry assistant = ReadEntries(fixture.Database).First(entry => entry.Role == 1);
    var assistantRef = new MessageRef(executionId, assistant.MessageId, MessageRole.Assistant, 0);
    MessageContentPage page = await fixture.Session.ReadMessageContentAsync(new(assistantRef, 0, 0));
    Assert.True(page.HasMore);
    Assert.InRange(Encoding.UTF8.GetByteCount(page.Text), 1, 16 * 1024);
    var savedAssistant = new StringBuilder(page.Text);
    MessageContentPage tail = page;
    while (tail.HasMore) {
      tail = await fixture.Session.ReadMessageContentAsync(tail.Next);
      savedAssistant.Append(tail.Text);
    }
    Assert.Equal(assistantText, savedAssistant.ToString());

    static async IAsyncEnumerable<ChatResponseUpdate> LargeToolRequest(
        [EnumeratorCancellation] CancellationToken token, string text, string argument) {
      await Task.CompletedTask;
      token.ThrowIfCancellationRequested();
      yield return Text(text);
      yield return new() {
        Contents = [new FunctionCallContent("large-call", "large", new Dictionary<string, object?> { ["payload"] = argument })]
      };
      yield return new() { FinishReason = ChatFinishReason.ToolCalls };
    }
  }

  private static StoredEntry[] ReadEntries(TemporaryDatabase database) {
    using SqliteConnection connection = database.OpenReadOnlyConnection();
    using SqliteCommand command = connection.CreateCommand();
    command.CommandText = """
      SELECT m.role, m.message_id, m.call_id, m.created_at,
          CASE WHEN m.role = 1 THEN CAST((SELECT text_utf8 FROM execution_output
              WHERE request_id = m.request_id AND chunk_index = m.first_chunk_index) AS TEXT) ELSE m.call_id END
      FROM execution_messages m ORDER BY m.ordinal;
      """;
    using SqliteDataReader reader = command.ExecuteReader();
    var entries = new List<StoredEntry>();
    while (reader.Read())
      entries.Add(new(reader.GetInt32(0), Guid.Parse(reader.GetString(1)),
          reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(3) ? null : reader.GetString(3)));
    return entries.ToArray();
  }

  private sealed record StoredEntry(int Role, Guid MessageId, string? Label, string? CreatedAt);

  [Fact]
  public void AToolSettledWithoutStartingHasNoExecutionTimes() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    Guid session = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
    Guid execution = Guid.NewGuid();
    store.UserMessages.Admit(session, 0,
        new(Guid.NewGuid(), 0, "request", TestLlmSelection.Default), true, execution);
    Guid request = store.Executions.ClaimNextRequest(session, execution, 0)!.RequestId;
    Assert.True(store.Executions.StartRequest(session, execution, 0, request));
    var call = new LlmToolCall("never-started", "fake", JsonSerializer.SerializeToElement(new { }));
    Assert.True(store.LlmResponses.Append(session, execution, 0, new(request, Guid.NewGuid(), 0, "", true)));
    Assert.True(store.LlmResponses.CompleteRequest(session, execution, 0,
        new(request, ExecutionOutcome.Completed, ToolCalls: [call])));

    Assert.Equal(ExecutionOutcome.Cancelled,
        store.Executions.TrySettle(session, execution, ExecutionOutcome.Cancelled)!.Outcome);
    Assert.False(Assert.Single(StoredTools.Read(database)).Started);
    using SqliteConnection connection = database.OpenReadOnlyConnection();
    using SqliteCommand command = connection.CreateCommand();
    command.CommandText = "SELECT started_at, ended_at FROM execution_tools;";
    using SqliteDataReader reader = command.ExecuteReader();
    Assert.True(reader.Read());
    Assert.True(reader.IsDBNull(0));
    Assert.True(reader.IsDBNull(1));
  }

  [Fact]
  public async Task PartialToolBatchNeverInvokesTools() {
    int effects = 0;
    AIFunction effect = AIFunctionFactory.Create(() => ++effects, name: "effect");
    var client = new ScriptedChatClient(PartialCall);
    await using var fixture = new ExecutionFixture(client, tools: [effect]);
    Guid executionId = await fixture.StartExecutionAsync("go");

    Assert.Equal(ExecutionOutcome.Failed,
        (await fixture.Session.WaitForSettlementAsync(executionId).WaitAsync(TestTimeout)).Outcome);
    Assert.Equal(0, effects);
    Assert.Equal(1, client.Calls);
    Assert.Empty(StoredTools.Read(fixture.Database));

    static async IAsyncEnumerable<ChatResponseUpdate> PartialCall(
        [EnumeratorCancellation] CancellationToken token) {
      await Task.CompletedTask;
      token.ThrowIfCancellationRequested();
      yield return new() { Contents = [new FunctionCallContent("call", "effect", null)] };
    }
  }
}
