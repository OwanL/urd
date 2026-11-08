using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Urd.Backend.Tests.Support;
using Urd.Core.Execution;
using Urd.Core.Providers;
using Urd.Core.Storage;
using Xunit;

namespace Urd.Backend.Tests.Storage;

public sealed class LlmProviderResponseStorageTests {
  [Fact]
  public void NativeEnvelopeSurvivesReopenAndReplacesDisplayTextInContext() {
    using var database = new TemporaryDatabase();
    LlmProviderResponse response = Response();
    Guid session, execution, request;
    using (UrdStore store = UrdStore.Open(database.Path)) {
      (session, execution, request) = Begin(store);
      FinalText(store, session, execution, request);
      Assert.True(store.LlmResponses.CompleteRequest(session, execution, 0,
          new(request, ExecutionOutcome.Completed, ProviderResponse: response)));
      Assert.Equal(response, Assert.Single(ReadProviderResponses(database)));
      Assert.Equal(ExecutionOutcome.Completed,
          store.Executions.TrySettle(session, execution, ExecutionOutcome.Completed)!.Outcome);
    }

    using UrdStore reopened = UrdStore.Open(database.Path);
    Guid nextExecution = Guid.NewGuid();
    reopened.UserMessages.Admit(session, 0,
        new(Guid.NewGuid(), 0, "next input", TestLlmSelection.Default), true, nextExecution);
    LlmRequest next = reopened.Executions.ClaimNextRequest(session, nextExecution, 0)!;
    Assert.Equal(new[] { ChatRole.User, ChatRole.Assistant, ChatRole.User },
        next.Messages.Select(message => message.Role));
    Assert.Equal(new[] { "request", "native answer", "next input" },
        next.Messages.Select(message => message.Text));
    ChatMessage assistant = next.Messages[1];
    Assert.Equal(response, Assert.IsType<LlmProviderResponse>(assistant.RawRepresentation));
    TextReasoningContent reasoning = Assert.Single(assistant.Contents.OfType<TextReasoningContent>());
    Assert.Equal("private reasoning", reasoning.Text);
    Assert.Equal("encrypted-state", reasoning.ProtectedData);
    Assert.Equal(response, Assert.Single(ReadProviderResponses(database)));
  }

  [Theory]
  [InlineData("execution_provider_context", "NEW.request_id IS NOT NULL")]
  [InlineData("execution_tools", "NEW.call_index = 1")]
  public void CompletionCommitFailureRollsBackNativeContextAndToolAuthorization(string table, string condition) {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    var (session, execution, request) = Begin(store);
    FinalText(store, session, execution, request);
    var completion = new LlmRequestResult(request, ExecutionOutcome.Completed,
        ToolCalls: [Call("one"), Call("two")], ProviderResponse: Response());
    database.Execute($"""
      CREATE TABLE test_parent (id INTEGER PRIMARY KEY);
      CREATE TABLE test_child (parent_id INTEGER REFERENCES test_parent(id) DEFERRABLE INITIALLY DEFERRED);
      CREATE TRIGGER fail_completion AFTER INSERT ON {table} WHEN {condition}
      BEGIN INSERT INTO test_child VALUES (1); END;
      """);

    Assert.Throws<SqliteException>(() => store.LlmResponses.CompleteRequest(session, execution, 0, completion));
    Assert.Null(Assert.Single(StoredExecutions.ReadRequests(database)).Outcome);
    Assert.Null(Assert.Single(StoredDeliveries.Read(database)).Outcome);
    Assert.Empty(ReadProviderResponses(database));
    Assert.Empty(StoredTools.Read(database));
    Assert.False(store.Tools.TryStart(session, execution, 0, request, "one"));

    database.Execute("DROP TRIGGER fail_completion;");
    Assert.True(store.LlmResponses.CompleteRequest(session, execution, 0, completion));
    Assert.Equal(completion.ProviderResponse, Assert.Single(ReadProviderResponses(database)));
    Assert.Equal(2, StoredTools.Read(database).Count);
  }

  private static (Guid Session, Guid Execution, Guid Request) Begin(UrdStore store) {
    Guid session = store.Sessions.CreatePrimary(Guid.NewGuid()).Id, execution = Guid.NewGuid();
    store.UserMessages.Admit(session, 0,
        new(Guid.NewGuid(), 0, "request", TestLlmSelection.Default), true, execution);
    return (session, execution, store.Executions.ClaimNextRequest(session, execution, 0)!.RequestId);
  }

  private static void FinalText(UrdStore store, Guid session, Guid execution, Guid request) =>
      Assert.True(store.LlmResponses.Append(session, execution, 0,
          new(request, Guid.NewGuid(), 0, "display answer", true)));

  private static LlmToolCall Call(string id) =>
      new(id, "fake", JsonSerializer.SerializeToElement(new { }));

  private static LlmProviderResponse Response() {
    const string json = """
      {"version":1,"items":[
        {"type":"reasoning","id":"rs_native","summary":[{"type":"summary_text","text":"private reasoning"}],"encrypted_content":"encrypted-state"},
        {"type":"message","id":"msg_native","role":"assistant","content":[{"type":"output_text","text":"native answer"}]}
      ]}
      """;
    return new(new(Guid.NewGuid(), 3, LlmApi.OpenAIResponses, "test-model"), json);
  }

  private static List<LlmProviderResponse> ReadProviderResponses(TemporaryDatabase database) {
    using SqliteConnection connection = database.OpenReadOnlyConnection();
    using SqliteCommand command = connection.CreateCommand();
    command.CommandText = "SELECT response_json FROM execution_provider_context ORDER BY request_id;";
    using SqliteDataReader reader = command.ExecuteReader();
    var responses = new List<LlmProviderResponse>();
    while (reader.Read())
      responses.Add(JsonSerializer.Deserialize<LlmProviderResponse>(reader.GetString(0))!);
    return responses;
  }
}
