using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using SkiaSharp;
using Urd.Backend.Tests.Support;
using Urd.Core.Compaction;
using Urd.Core.Execution;
using Urd.Core.Preferences;
using Urd.Core.Providers;
using Urd.Core.Sessions;
using Urd.Core.Storage;
using Urd.Core.Tools;
using Xunit;
using static Urd.Backend.Tests.Support.TestAsync;

#pragma warning disable MEAI001

namespace Urd.Backend.Tests.Execution;

public sealed class ProviderSessionExecutionTests {
  private const string ToolOutputText = "lookup result text";
  private const string ToolDetailsMarker = "PRIVATE_TOOL_DETAILS";
  private const string ImageOmissionLabel = "[Images omitted because the current model does not support images.]";

  [Theory]
  [InlineData(LlmApi.OpenAIResponses, false)]
  [InlineData(LlmApi.OpenAIChatCompletions, false)]
  [InlineData(LlmApi.OpenAIResponses, true)]
  [InlineData(LlmApi.OpenAIChatCompletions, true)]
  public async Task ManagedToolLoopPersistsNativeContextAndReplaysItAfterReopening(LlmApi api, bool background) {
    using var database = new TemporaryDatabase();
    using var timeout = new CancellationTokenSource(TestTimeout);
    TaskCompletionSource release = Signal();
    bool omitCallItemDone = api == LlmApi.OpenAIResponses && !background;
    string stream = ToolResponse(api, background, correction: api == LlmApi.OpenAIResponses, omitCallItemDone: omitCallItemDone);
    using var first = new ScriptedOpenAIHandler(stream) { ReadGate = DraftGate(api, stream, release.Task) };
    using var done = new ScriptedOpenAIHandler(api == LlmApi.OpenAIResponses
        ? ProviderModelStreams.Responses([ProviderModelStreams.Message.Replace("hello", "done")], text: "done")
            .Replace("msg_fixture", "msg_done").Replace("resp_fixture", "resp_done")
        : OpenAIStreams.ChatStream().Replace("hello", "done").Replace("chatcmpl_fixture", "chatcmpl_done"));
    using var firstTransport = new HttpMessageInvoker(first, disposeHandler: false);
    using var doneTransport = new HttpMessageInvoker(done, disposeHandler: false);
    int providerCalls = 0, effects = 0;
    byte[] imageBytes = TinyPng();
    var lookup = new TestAgentTool("lookup", (arguments, context, cancellation) => {
      Assert.Equal("abc", arguments.GetProperty("key").GetString());
      Assert.False(arguments.TryGetProperty("background", out _));
      Assert.False(cancellation.IsCancellationRequested);
      Assert.True(context.ModelAcceptsImages);
      Assert.Equal(1, first.StreamDisposals);
      StoredRequest request = Assert.Single(StoredExecutions.ReadRequests(database));
      Assert.Equal(ExecutionOutcome.Completed, request.Outcome);
      var providerContext = Assert.Single(ReadProviderResponses(database));
      Assert.Equal(request.RequestId, providerContext.Key);
      Assert.Equal(api, providerContext.Value.Scope.Api);
      Assert.Equal("fixture-model", providerContext.Value.Scope.ModelId);
      ChatMessage restored = LlmProviderContext.Restore(providerContext.Value);
      Assert.Equal("hello", restored.Text);
      Assert.Single(restored.Contents.OfType<TextReasoningContent>());
      Assert.Equal("call_fixture", Assert.Single(restored.Contents.OfType<FunctionCallContent>()).CallId);
      StoredTool tool = Assert.Single(StoredTools.Read(database));
      Assert.Equal(request.RequestId, tool.RequestId);
      Assert.True(tool.Started);
      Assert.Null(tool.ResultJson);
      Interlocked.Increment(ref effects);
      return Task.FromResult(new ToolOutput(ToolOutputText, [new DataContent(imageBytes, "image/png")],
          JsonSerializer.SerializeToElement(new { privateMarker = ToolDetailsMarker })));
    });
    var initialHandler = new ControlledProviderHandler(Respond);
    Guid sessionId, connectionId;
    string storedResult;
    Dictionary<Guid, LlmProviderResponse> initialContexts;
    string[] startingInstructions;
    await using (var fixture = new ManagedSession(database, initialHandler)) {
      await fixture.OpenAsync(api, new ToolCatalog([lookup]));
      sessionId = fixture.Session.Id;
      connectionId = fixture.Connection.Id;
      var changes = Channel.CreateUnbounded<ToolDraftUpdate>();
      using var watcher = await WatchDrafts(fixture.Session, changes, timeout.Token);
      var selection = new LlmSelection(fixture.Connection, "fixture-model", null, ModelFamily.Other, true);
      UserMessageReceipt receipt = await fixture.Session.SubmitUserMessageAsync(new(Guid.NewGuid(), 0, "go", selection));
      ToolDraftUpdate draft;
      try {
        draft = await AssertDraftAsync(api == LlmApi.OpenAIResponses ? "drafting" : "drafted", background,
            receipt, changes, fixture.Session, timeout.Token, correction: api == LlmApi.OpenAIResponses);
        Assert.Equal(0, effects);
      } finally {
        release.TrySetResult();
      }
      Assert.Equal(ExecutionOutcome.Completed, (await fixture.Session.WaitForExecutionFinishAsync(receipt.ExecutionId!.Value)
          .WaitAsync(timeout.Token)).Outcome);
      if (api == LlmApi.OpenAIResponses && !omitCallItemDone) {
        await ReadDraftAsync(changes, "drafted", timeout.Token);
        ToolDraftUpdate corrected = await ReadDraftAsync(changes, "drafted", timeout.Token);
        Assert.Equal(draft.EntryId, corrected.EntryId);
        Assert.Equal(background ? "{\"key\":\"abc\",\"background\":true}" : "{\"key\":\"abc\"}", corrected.Head + corrected.Tail);
      }
      if (omitCallItemDone)
        Assert.Equal("stray", (await ReadDraftAsync(changes, "discarded", timeout.Token)).CallId);
      ToolDraftUpdate promoted = await ReadDraftAsync(changes, "promoted", timeout.Token);
      Assert.Equal(draft.EntryId, promoted.EntryId);
      if (omitCallItemDone) Assert.Equal("{\"key\":\"abc\"}", promoted.Head + promoted.Tail);
      TranscriptHistoryPage history = await fixture.Session.Transcript.ReadHistoryAsync(new(TranscriptHistoryMode.Latest, 0));
      Assert.Equal(draft.EntryId, Assert.Single(history.Entries, entry => entry.Entry.Kind == TranscriptEntryKind.Tool).Entry.EntryId);

      Assert.Equal(2, providerCalls);
      Assert.Equal(1, effects);
      Assert.Equal(2, initialHandler.Requests.Count);
      Assert.All(initialHandler.Requests, request => Assert.Null(request.Authorization));
      Assert.All(first.Requests.Concat(done.Requests), request => Assert.Empty(request.SessionIds!));
      startingInstructions = WireInstructions(api, initialHandler.Requests[0]);
      Assert.NotEmpty(startingInstructions);
      Assert.Equal(startingInstructions, WireInstructions(api, initialHandler.Requests[1]));
      StoredTool tool = Assert.Single(StoredTools.Read(database));
      Assert.Equal(ExecutionOutcome.Completed, tool.Outcome);
      storedResult = Assert.IsType<string>(tool.ResultJson);
      AssertStoredOutput(storedResult, imageBytes);
      AssertNativeReplay(api, initialHandler.Requests[1], followup: false, supportsImages: true, imageBytes, background);
      initialContexts = AssertCompletedContexts(database, 2, connectionId, api);
      Assert.Contains(tool.RequestId, initialContexts.Keys);
      Assert.Equal(1, first.StreamDisposals);
      Assert.Equal(1, done.StreamDisposals);
      await AssertPublicTextAsync(fixture.Session, "hello", "done");
    }
    Assert.Equal(1, initialHandler.DisposeCalls);

    var reopenedHandler = new ControlledProviderHandler(Respond);
    await using (var reopened = new ManagedSession(database, reopenedHandler)) {
      await reopened.OpenAsync(api, new ToolCatalog([lookup]), sessionId, connectionId);
      Assert.Equal(sessionId, reopened.Session.Id);
      await AssertPublicTextAsync(reopened.Session, "hello", "done");
      await reopened.SendAsync("followup", supportsImages: false);

      Assert.Equal(3, providerCalls);
      Assert.Equal(1, effects);
      ScriptedOpenAIRequest third = Assert.Single(reopenedHandler.Requests);
      Assert.Null(third.Authorization);
      Assert.Equal(startingInstructions, WireInstructions(api, third));
      AssertNativeReplay(api, third, followup: true, supportsImages: false, imageBytes, background);
      StoredTool savedTool = Assert.Single(StoredTools.Read(database));
      Assert.Equal(storedResult, savedTool.ResultJson);
      AssertStoredOutput(savedTool.ResultJson!, imageBytes);
      AssertRestoredOutput(reopened.ReadOutput(savedTool.RequestId, savedTool.CallId), imageBytes);
      Dictionary<Guid, LlmProviderResponse> contexts = AssertCompletedContexts(database, 3, connectionId, api);
      foreach (var context in initialContexts)
        Assert.Equal(context.Value, contexts[context.Key]);
      Assert.Equal(2, StoredExecutions.Read(database).Count);
      Assert.All(StoredExecutions.Read(database), execution => Assert.Equal(ExecutionOutcome.Completed, execution.Outcome));
      Assert.Equal(2, done.StreamDisposals);
      Assert.All(done.Requests, request => Assert.Empty(request.SessionIds!));
      await AssertPublicTextAsync(reopened.Session, "hello", "done", "done");
    }
    Assert.Equal(1, reopenedHandler.DisposeCalls);
    Assert.Single(first.Requests);
    Assert.Equal(2, done.Requests.Count);

    Task<HttpResponseMessage> Respond(HttpRequestMessage request, CancellationToken token) =>
        Interlocked.Increment(ref providerCalls) switch {
          1 => firstTransport.SendAsync(request, token),
          2 or 3 => doneTransport.SendAsync(request, token),
          _ => throw new InvalidOperationException("Unexpected provider request.")
        };
  }

  [Fact]
  public async Task ChatCompletionsInterleavedToolDraftsStayDraftingUntilFinish() {
    using var database = new TemporaryDatabase();
    using var timeout = new CancellationTokenSource(TestTimeout);
    TaskCompletionSource release = Signal();
    string Chunk(object delta, string? finishReason = null) => "data: " + JsonSerializer.Serialize(new {
      id = "chatcmpl_fixture", @object = "chat.completion.chunk", created = 1700000000, model = "fixture-model",
      choices = new[] { new { index = 0, delta, finish_reason = finishReason } }
    }) + "\n\n";
    string finish = Chunk(new Dictionary<string, object?>(), "tool_calls");
    string stream = Chunk(new { role = "assistant", content = "hello" }) +
        Chunk(new { tool_calls = new[] { new { index = 0, id = "call_first", type = "function",
          function = new { name = "lookup", arguments = "{\"key\":\"first" } } } }) +
        Chunk(new { tool_calls = new[] { new { index = 1, id = "call_second", type = "function",
          function = new { name = "lookup", arguments = "{\"key\":\"second" } } } }) +
        Chunk(new { tool_calls = new[] { new { index = 0, function = new { arguments = "\"}" } } } }) +
        Chunk(new { tool_calls = new[] { new { index = 1, function = new { arguments = "\"}" } } } }) +
        finish + "data: [DONE]\n\n";
    int finishOffset = stream.IndexOf(finish, StringComparison.Ordinal);
    using var first = new ScriptedOpenAIHandler(stream) {
      ReadGate = (Encoding.UTF8.GetByteCount(stream.AsSpan(0, finishOffset)), release.Task)
    };
    using var done = new ScriptedOpenAIHandler(OpenAIStreams.ChatStream());
    using var firstTransport = new HttpMessageInvoker(first, disposeHandler: false);
    using var doneTransport = new HttpMessageInvoker(done, disposeHandler: false);
    int providerCalls = 0, effects = 0;
    var handler = new ControlledProviderHandler((request, cancellation) =>
        Interlocked.Increment(ref providerCalls) == 1
            ? firstTransport.SendAsync(request, cancellation)
            : doneTransport.SendAsync(request, cancellation));
    AIFunction lookup = AIFunctionFactory.Create((string key) => {
      Assert.Contains(key, new[] { "first", "second" });
      Interlocked.Increment(ref effects);
      return key;
    }, name: "lookup");

    await using var fixture = new ManagedSession(database, handler);
    await fixture.OpenAsync(LlmApi.OpenAIChatCompletions, [lookup]);
    var changes = Channel.CreateUnbounded<ToolDraftUpdate>();
    using var watcher = await WatchDrafts(fixture.Session, changes, timeout.Token);
    var selection = new LlmSelection(fixture.Connection, "fixture-model", null, ModelFamily.Other, false);
    UserMessageReceipt receipt = await fixture.Session.SubmitUserMessageAsync(new(Guid.NewGuid(), 0, "go", selection));
    var beforeFinish = new Dictionary<string, ToolDraftUpdate>(StringComparer.Ordinal);
    try {
      while (beforeFinish.Count < 2) {
        ToolDraftUpdate draft = await ReadDraftAsync(changes, "drafting", timeout.Token);
        beforeFinish[draft.CallId] = draft;
      }
      Assert.Equal(new[] { "call_first", "call_second" }, beforeFinish.Keys.Order(StringComparer.Ordinal));
      Assert.Equal("{\"key\":\"first", beforeFinish["call_first"].Head + beforeFinish["call_first"].Tail);
      Assert.Equal("{\"key\":\"second", beforeFinish["call_second"].Head + beforeFinish["call_second"].Tail);
      await Task.Delay(TimeSpan.FromMilliseconds(200), timeout.Token);
      while (changes.Reader.TryRead(out var update)) {
        Assert.Equal("drafting", update.State);
        beforeFinish[update.CallId] = update;
      }
      Assert.Equal(0, effects);
    } finally {
      release.TrySetResult();
    }

    Assert.Equal(ExecutionOutcome.Completed, (await fixture.Session.WaitForExecutionFinishAsync(
        Assert.IsType<Guid>(receipt.ExecutionId)).WaitAsync(timeout.Token)).Outcome);
    Assert.Equal(2, effects);
    var drafted = new Dictionary<string, ToolDraftUpdate>(StringComparer.Ordinal);
    while (drafted.Count < 2) {
      ToolDraftUpdate update = await ReadDraftAsync(changes, "drafted", timeout.Token);
      drafted[update.CallId] = update;
    }
    foreach (string callId in beforeFinish.Keys) {
      Assert.Equal(beforeFinish[callId].EntryId, drafted[callId].EntryId);
      Assert.Equal(callId == "call_first" ? "{\"key\":\"first\"}" : "{\"key\":\"second\"}",
          drafted[callId].Head + drafted[callId].Tail);
    }
    var promoted = new Dictionary<string, ToolDraftUpdate>(StringComparer.Ordinal);
    while (promoted.Count < 2) {
      ToolDraftUpdate update = await ReadDraftAsync(changes, "promoted", timeout.Token);
      promoted[update.CallId] = update;
    }
    foreach (string callId in beforeFinish.Keys)
      Assert.Equal(beforeFinish[callId].EntryId, promoted[callId].EntryId);
    TranscriptHistoryPage history = await fixture.Session.Transcript.ReadHistoryAsync(new(TranscriptHistoryMode.Latest, 0));
    Assert.Equal(2, history.Entries.Count(entry => entry.Entry.Kind == TranscriptEntryKind.Tool));
  }

  [Fact]
  public async Task ChatGptNamespacedToolLoopReplaysStableInputAndSettingsAfterReopening() {
    using var database = new TemporaryDatabase();
    using var identity = new FakeOpenAIIdentity();
    string call = ProviderModelStreams.Call.Replace("\"name\":\"lookup\"", "\"namespace\":\"urd\",\"name\":\"lookup\"");
    string reasoning = ProviderModelStreams.Reasoning.Replace("\"summary\":[]", "\"summary\":[{\"type\":\"summary_text\",\"text\":\"thought\"}]");
    using var first = new ScriptedOpenAIHandler(ProviderModelStreams.Responses([ProviderModelStreams.Message, reasoning, call]));
    using var done = new ScriptedOpenAIHandler(ProviderModelStreams.Responses(
        [ProviderModelStreams.Message.Replace("hello", "done").Replace("msg_fixture", "msg_done")], text: "done"));
    using var transport = new ChatGptLoopTransport(identity, first, done);
    using var store = UrdStore.Open(database.Path);
    using var manager = new ProviderManager(store, () => FakeOpenAIIdentity.Callback, transport, FakeOpenAIIdentity.Issuer);
    PrimarySession? session = null;
    SubagentSession? child = null;
    IChatClient? client = null;
    try {
      ProviderConnection connection = await manager.AddChatGptAsync("Synthetic ChatGPT");
      ProviderSignIn signIn = await manager.BeginSignInAsync(connection.Id);
      Assert.Equal(ProviderSignInStatus.Pending, signIn.Status);
      Uri callback = new(await identity.AuthorizeAsync(signIn.AuthorizationUrl!));
      await manager.CompleteSignInAsync(FakeOpenAIIdentity.Parse(callback.Query)["state"], callback.AbsoluteUri).WaitAsync(TestTimeout);
      IChatClient activeClient = manager.CreateChatClient(connection);
      client = activeClient;
      int effects = 0;
      var lookup = new TestAgentTool("lookup", (arguments, context, cancellation) => {
        Assert.Equal("abc", arguments.GetProperty("key").GetString());
        Interlocked.Increment(ref effects);
        return Task.FromResult(new ToolOutput(ToolOutputText, []));
      });
      var tools = new ToolCatalog([lookup]);
      SessionRecord record = store.Sessions.CreatePrimary(Guid.NewGuid(), Path.GetDirectoryName(database.Path)!);
      var selection = new LlmSelection(connection, "fixture-model", null, ModelFamily.Other, false);
      session = new(record, store, _ => activeClient, tools);
      await Send(session, "go", selection);
      await session.StopAcceptingUserMessagesAsync();
      await session.StopExecutionAsync().WaitAsync(TestTimeout);
      session = new(Assert.IsType<SessionRecord>(store.Sessions.Read(record.Id)), store, _ => activeClient, tools);
      await Send(session, "followup", selection);
      Assert.Equal(1, effects);
      Assert.Equal(3, transport.Requests.Count);
      JsonObject[] requests = transport.Requests.Select(request => JsonNode.Parse(request.Body)!.AsObject()).ToArray();
      Assert.All(transport.Requests, request => Assert.Equal("/v1/responses", request.Uri.AbsolutePath));
      JsonArray[] inputs = requests.Select(request => request["input"]!.AsArray()).ToArray();
      Assert.All(requests, request => {
        Assert.Null(request["instructions"]);
        Assert.Equal("fixture-model", (string?)request["model"]);
        Assert.True((bool)request["stream"]!);
        Assert.False((bool)request["store"]!);
        JsonObject ns = Assert.Single(request["tools"]!.AsArray())!.AsObject();
        Assert.Equal("namespace", (string?)ns["type"]);
        Assert.Equal("urd", (string?)ns["name"]);
        Assert.Equal("lookup", (string?)Assert.Single(ns["tools"]!.AsArray())!["name"]);
      });
      Assert.Equal("developer", (string?)inputs[0][0]!["role"]);
      Assert.Contains(inputs[0], item => (string?)item!["role"] == "developer" &&
          WireText(JsonSerializer.SerializeToElement(item["content"])).Contains("# Default working guidance", StringComparison.Ordinal));
      Assert.Equal("go", WireText(JsonSerializer.SerializeToElement(inputs[0][^1]!["content"])));
      for (int i = 1; i < inputs.Length; i++) {
        JsonObject baseline = (JsonObject)requests[0].DeepClone();
        JsonObject current = (JsonObject)requests[i].DeepClone();
        baseline.Remove("input");
        current.Remove("input");
        Assert.True(JsonNode.DeepEquals(baseline, current));
        for (int j = 0; j < inputs[i - 1].Count; j++)
          Assert.True(JsonNode.DeepEquals(inputs[i - 1][j], inputs[i][j]));
      }
      JsonNode reasoningInput = inputs[1][^3]!;
      JsonNode callInput = inputs[1][^2]!;
      JsonNode resultInput = inputs[1][^1]!;
      Assert.Equal("reasoning", (string?)reasoningInput["type"]);
      Assert.Equal("private-reasoning", (string?)reasoningInput["encrypted_content"]);
      Assert.Equal("thought", (string?)reasoningInput["summary"]![0]!["text"]);
      Assert.Equal("function_call", (string?)callInput["type"]);
      Assert.Equal("urd", (string?)callInput["namespace"]);
      Assert.Equal("call_fixture", (string?)callInput["call_id"]);
      Assert.Equal("abc", (string?)JsonNode.Parse((string)callInput["arguments"]!)!["key"]);
      Assert.Equal("function_call_output", (string?)resultInput["type"]);
      Assert.Equal("call_fixture", (string?)resultInput["call_id"]);
      Assert.Contains(ToolOutputText, resultInput["output"]!.ToJsonString(), StringComparison.Ordinal);
      Assert.Equal("followup", WireText(JsonSerializer.SerializeToElement(inputs[2][^1]!["content"])));
      Assert.All(first.Requests.Concat(done.Requests), request => AssertSessionHeader(request, record.Id));

      await session.StopAcceptingUserMessagesAsync();
      await session.StopExecutionAsync().WaitAsync(TestTimeout);
      SessionRecord otherRecord = store.Sessions.CreatePrimary(Guid.NewGuid(), Path.GetDirectoryName(database.Path)!);
      Assert.NotEqual(record.Id, otherRecord.Id);
      session = new(otherRecord, store, _ => activeClient, tools);
      await Send(session, "other", selection);
      Assert.Equal(4, transport.Requests.Count);
      AssertSessionHeader(done.Requests[^1], otherRecord.Id);

      await session.StopAcceptingUserMessagesAsync();
      await session.StopExecutionAsync().WaitAsync(TestTimeout);
      session = null;
      SessionRecord childRecord = store.Sessions.CreateSubagent(Guid.NewGuid(), record.Id, "owning-call");
      Assert.NotEqual(record.Id, childRecord.Id);
      child = new(Assert.IsType<SessionRecord>(store.Sessions.Read(childRecord.Id)), store, _ => activeClient, tools);
      Guid childExecution = await child.StartAssignmentAsync("child task", null, selection).WaitAsync(TestTimeout);
      Assert.Equal(ExecutionOutcome.Completed,
          (await child.WaitForExecutionFinishAsync(childExecution).WaitAsync(TestTimeout)).Outcome);
      Assert.Equal(5, transport.Requests.Count);
      AssertSessionHeader(done.Requests[^1], childRecord.Id);
      Assert.DoesNotContain(record.Id.ToString(), done.Requests[^1].SessionIds!);
    } finally {
      try {
        if (child is not null) {
          await child.StopAcceptingUserMessagesAsync();
          await child.StopExecutionAsync().WaitAsync(TestTimeout);
        }
        if (session is not null) {
          await session.StopAcceptingUserMessagesAsync();
          await session.StopExecutionAsync().WaitAsync(TestTimeout);
        }
      } finally {
        try {
          await manager.ShutdownAsync().WaitAsync(TestTimeout);
        } finally {
          client?.Dispose();
        }
      }
    }

    async Task Send(PrimarySession target, string text, LlmSelection selection) {
      UserMessageReceipt receipt = await target.SubmitUserMessageAsync(new(Guid.NewGuid(), 0, text, selection));
      Assert.Equal(MessageAcceptance.Accepted, receipt.Acceptance);
      Assert.Equal(ExecutionOutcome.Completed,
          (await target.WaitForExecutionFinishAsync(Assert.IsType<Guid>(receipt.ExecutionId)).WaitAsync(TestTimeout)).Outcome);
    }
  }

  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public async Task ChatGptNamespacedToolLoopUsesSocketContinuation(bool failBeforeCompletion) {
    using var database = new TemporaryDatabase();
    using var identity = new FakeOpenAIIdentity();
    string call = ProviderModelStreams.Call.Replace("\"name\":\"lookup\"", "\"namespace\":\"urd\",\"name\":\"lookup\"");
    string reasoning = ProviderModelStreams.Reasoning.Replace("\"summary\":[]", "\"summary\":[{\"type\":\"summary_text\",\"text\":\"thought\"}]");
    string firstStream = ReorderCompletedOutput(ProviderModelStreams.Responses([ProviderModelStreams.Message, reasoning, call],
        terminal: failBeforeCompletion ? null : "response.completed"));
    string doneStream = ProviderModelStreams.Responses(
        [ProviderModelStreams.Message.Replace("hello", "done").Replace("msg_fixture", "msg_done")], text: "done")
        .Replace("resp_fixture", "resp_done");
    string followupStream = doneStream.Replace("resp_done", "resp_followup").Replace("msg_done", "msg_followup");
    using var first = new ScriptedOpenAIHandler(firstStream);
    using var done = new ScriptedOpenAIHandler(doneStream);
    using var transport = new ChatGptLoopTransport(identity, first, done);
    using var store = UrdStore.Open(database.Path);
    var sockets = new List<LoopSocket>();
    var handshakes = new List<(string Token, Guid Session)>();
    Task<WebSocket> Connect(string token, Guid sessionId, CancellationToken cancellation) {
      cancellation.ThrowIfCancellationRequested();
      handshakes.Add((token, sessionId));
      string[] streams = [followupStream];
      if (sockets.Count == 0)
        streams = failBeforeCompletion ? [firstStream] : [firstStream, doneStream];
      var socket = new LoopSocket(streams);
      sockets.Add(socket);
      return Task.FromResult<WebSocket>(socket);
    }
    using var manager = new ProviderManager(store, () => FakeOpenAIIdentity.Callback, transport, FakeOpenAIIdentity.Issuer,
        connectSocket: Connect);
    PrimarySession? session = null;
    IChatClient? client = null;
    LlmSelection selection = null!;
    try {
      ProviderConnection connection = await manager.AddChatGptAsync("Synthetic ChatGPT");
      ProviderSignIn signIn = await manager.BeginSignInAsync(connection.Id);
      Uri callback = new(await identity.AuthorizeAsync(signIn.AuthorizationUrl!));
      await manager.CompleteSignInAsync(FakeOpenAIIdentity.Parse(callback.Query)["state"], callback.AbsoluteUri).WaitAsync(TestTimeout);
      client = manager.CreateChatClient(connection);
      int effects = 0;
      var tools = new ToolCatalog([new TestAgentTool("lookup", (arguments, _, _) => {
        Assert.Equal("abc", arguments.GetProperty("key").GetString());
        Interlocked.Increment(ref effects);
        return Task.FromResult(new ToolOutput(ToolOutputText, []));
      })]);
      SessionRecord record = store.Sessions.CreatePrimary(Guid.NewGuid(), Path.GetDirectoryName(database.Path)!);
      selection = new LlmSelection(connection, "fixture-model", null, ModelFamily.Other, false);
      session = new(record, store, _ => client, tools);
      SessionExecutionResult result = await Send(session, "go");
      Assert.Single(sockets);
      Assert.Equal(("synthetic-access-1", record.Id), Assert.Single(handshakes));
      Assert.Empty(transport.Requests);
      if (failBeforeCompletion) {
        Assert.Equal(ExecutionOutcome.Failed, result.Outcome);
        Assert.Contains("The request may have been processed.", (await session.ReadSnapshotAsync()).LastSettlement?.Error,
            StringComparison.Ordinal);
        Assert.Equal(0, effects);
        Assert.Empty(StoredTools.Read(database));
        Assert.Empty(ReadProviderResponses(database));
        Assert.Single(sockets[0].Requests);
        Assert.True(sockets[0].Disposed);
        return;
      }
      Assert.Equal(ExecutionOutcome.Completed, result.Outcome);
      Assert.Equal(1, effects);
      Assert.Equal(2, sockets[0].Requests.Count);
      JsonObject initial = sockets[0].Requests[0];
      JsonObject next = sockets[0].Requests[1];
      Assert.Equal("response.create", (string?)initial["type"]);
      Assert.Equal("response.create", (string?)next["type"]);
      Assert.Null(initial["previous_response_id"]);
      JsonObject initialSettings = (JsonObject)initial.DeepClone();
      JsonObject nextSettings = (JsonObject)next.DeepClone();
      initialSettings.Remove("input");
      nextSettings.Remove("input");
      nextSettings.Remove("previous_response_id");
      Assert.True(JsonNode.DeepEquals(initialSettings, nextSettings));
      Assert.Equal("go", WireText(JsonSerializer.SerializeToElement(initial["input"]!.AsArray()[^1]!["content"])));
      Assert.True((string?)next["previous_response_id"] == "resp_fixture",
          "Expected socket continuation with previous_response_id=resp_fixture; SDK tool-loop request was sent as full input instead: " + next.ToJsonString());
      JsonNode output = Assert.Single(next["input"]!.AsArray())!;
      Assert.Equal("function_call_output", (string?)output["type"]);
      Assert.Equal("call_fixture", (string?)output["call_id"]);
      Assert.Contains(ToolOutputText, output["output"]!.ToJsonString(), StringComparison.Ordinal);
      await session.StopAcceptingUserMessagesAsync();
      await session.StopExecutionAsync().WaitAsync(TestTimeout);
      Assert.True(sockets[0].Disposed);
      session = new(Assert.IsType<SessionRecord>(store.Sessions.Read(record.Id)), store, _ => client, tools);
      Assert.Equal(ExecutionOutcome.Completed, (await Send(session, "followup")).Outcome);
      Assert.Equal(2, sockets.Count);
      Assert.Equal(("synthetic-access-1", record.Id), handshakes[1]);
      JsonObject reopened = Assert.Single(sockets[1].Requests);
      Assert.Null(reopened["previous_response_id"]);
      Assert.Equal("followup", WireText(JsonSerializer.SerializeToElement(reopened["input"]!.AsArray()[^1]!["content"])));
      Assert.Contains(reopened["input"]!.AsArray(), item => (string?)item!["type"] == "function_call_output");
      Assert.Equal(1, effects);
      Assert.Empty(transport.Requests);
    } finally {
      if (session is not null) {
        await session.StopAcceptingUserMessagesAsync();
        await session.StopExecutionAsync().WaitAsync(TestTimeout);
      }
      await manager.ShutdownAsync().WaitAsync(TestTimeout);
      client?.Dispose();
    }

    async Task<SessionExecutionResult> Send(PrimarySession target, string text) {
      UserMessageReceipt receipt = await target.SubmitUserMessageAsync(new(Guid.NewGuid(), 0, text, selection));
      Assert.Equal(MessageAcceptance.Accepted, receipt.Acceptance);
      return await target.WaitForExecutionFinishAsync(Assert.IsType<Guid>(receipt.ExecutionId)).WaitAsync(TestTimeout);
    }
  }

  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public async Task ChatGptCompactionRetiresTheOldSocketBeforeSendingCompactedHistory(bool handshakeFails) {
    using var database = new TemporaryDatabase();
    using var identity = new FakeOpenAIIdentity();
    using var store = UrdStore.Open(database.Path);
    var preferences = new PreferenceManager(store);
    const string summary = "Earlier history summary";
    string call = ProviderModelStreams.Call.Replace("\"name\":\"lookup\"", "\"namespace\":\"urd\",\"name\":\"lookup\"");
    string initialStream = ProviderModelStreams.Responses([ProviderModelStreams.Message, call]);
    string finalStream = ProviderModelStreams.Responses().Replace("resp_fixture", "resp_after_compaction");
    using var summaryHandler = new ScriptedOpenAIHandler(ProviderModelStreams.Responses().Replace("hello", summary));
    using var fallbackHandler = new ScriptedOpenAIHandler(finalStream);
    using var transport = new ChatGptLoopTransport(identity, summaryHandler, fallbackHandler);
    using var oldSocket = new LoopSocket([initialStream], closeWhenExhausted: true);
    using var freshSocket = new LoopSocket([finalStream]);
    int handshakes = 0;
    bool retiredBeforeHandshake = false;
    Task<WebSocket> Connect(string token, Guid sessionId, CancellationToken cancellation) {
      cancellation.ThrowIfCancellationRequested();
      if (++handshakes == 1)
        return Task.FromResult<WebSocket>(oldSocket);
      retiredBeforeHandshake = oldSocket.Disposed;
      return handshakeFails ? Task.FromException<WebSocket>(new WebSocketException()) : Task.FromResult<WebSocket>(freshSocket);
    }
    using var manager = new ProviderManager(store, () => FakeOpenAIIdentity.Callback, transport, FakeOpenAIIdentity.Issuer,
        connectSocket: Connect);
    ProviderConnection connection = await manager.AddChatGptAsync("Synthetic ChatGPT");
    ProviderSignIn signIn = await manager.BeginSignInAsync(connection.Id);
    Uri callback = new(await identity.AuthorizeAsync(signIn.AuthorizationUrl!));
    await manager.CompleteSignInAsync(FakeOpenAIIdentity.Parse(callback.Query)["state"], callback.AbsoluteUri).WaitAsync(TestTimeout);
    using IChatClient client = manager.CreateChatClient(connection);
    int effects = 0;
    var tools = new ToolCatalog([new TestAgentTool("lookup", (_, _, _) => {
      Interlocked.Increment(ref effects);
      preferences.SetCompactionTriggerTokenCount(8_192);
      return Task.FromResult(new ToolOutput(ToolOutputText, []));
    })]);
    var session = new PrimarySession(store.Sessions.CreatePrimary(Guid.NewGuid(), Path.GetDirectoryName(database.Path)!),
        store, _ => client, tools);
    try {
      var selection = new LlmSelection(connection, "fixture-model", null, ModelFamily.Other, false);
      string removedText = new('x', 50_000);
      UserMessageReceipt receipt = await session.SubmitUserMessageAsync(new(Guid.NewGuid(), 0, removedText, selection));
      Assert.Equal(MessageAcceptance.Accepted, receipt.Acceptance);
      SessionExecutionResult result = await session.WaitForExecutionFinishAsync(Assert.IsType<Guid>(receipt.ExecutionId)).WaitAsync(TestTimeout);

      SavedHistoryCompaction boundary = Assert.IsType<SavedHistoryCompaction>(store.Compactions.ReadContext(session.Id, 0).PreviousCompaction);
      Assert.Equal(CompactionReason.Threshold, (await session.ReadCompactionAsync(boundary.Id, 0))?.Reason);
      Assert.True(result.Outcome == ExecutionOutcome.Completed, (await session.ReadSnapshotAsync()).LastSettlement?.Error);
      Assert.Equal(2, handshakes);
      Assert.True(retiredBeforeHandshake);
      Assert.Single(oldSocket.Requests);
      Assert.Equal(1, effects);
      Assert.Single(StoredTools.Read(database));
      Assert.Contains("Summarize the removed conversation history", transport.Requests[0].Body, StringComparison.Ordinal);
      JsonObject compacted;
      if (handshakeFails) {
        Assert.Empty(freshSocket.Requests);
        Assert.Equal(2, transport.Requests.Count);
        compacted = JsonNode.Parse(transport.Requests[1].Body)!.AsObject();
      } else {
        Assert.Single(transport.Requests);
        compacted = Assert.Single(freshSocket.Requests);
      }
      Assert.Null(compacted["previous_response_id"]);
      string input = compacted["input"]!.ToJsonString();
      Assert.Contains(summary, input, StringComparison.Ordinal);
      Assert.DoesNotContain(removedText[..128], input, StringComparison.Ordinal);
      Assert.Contains(compacted["input"]!.AsArray(), item => (string?)item!["type"] == "function_call" &&
          (string?)item["call_id"] == "call_fixture");
      Assert.Contains(compacted["input"]!.AsArray(), item => (string?)item!["type"] == "function_call_output" &&
          (string?)item["call_id"] == "call_fixture");
    } finally {
      await session.StopAcceptingUserMessagesAsync();
      await session.StopExecutionAsync().WaitAsync(TestTimeout);
      await manager.ShutdownAsync().WaitAsync(TestTimeout);
    }
  }

  [Theory]
  [InlineData(LlmApi.OpenAIResponses)]
  [InlineData(LlmApi.OpenAIChatCompletions)]
  [InlineData(LlmApi.OpenAIResponses, true)]
  public async Task TransportFailureOrStopWithholdsEffectsAndContext(LlmApi api, bool stop = false) {
    using var database = new TemporaryDatabase();
    // Chat's [DONE] would let the SDK stop without observing the failing stream tail.
    using var timeout = new CancellationTokenSource(TestTimeout);
    TaskCompletionSource release = Signal();
    string stream = ToolResponse(api).Replace("data: [DONE]\n\n", "");
    var handler = new ScriptedOpenAIHandler(stream) {
      ReadGate = DraftGate(api, stream, release.Task, argumentsDone: true), FailAtEnd = true
    };
    int effects = 0;
    AIFunction lookup = AIFunctionFactory.Create((string key) => Interlocked.Increment(ref effects), name: "lookup");
    Guid sessionId;
    Guid connectionId;
    SessionExecutionResult settlement;
    await using (var fixture = new ManagedSession(database, handler)) {
      await fixture.OpenAsync(api, [lookup]);
      sessionId = fixture.Session.Id;
      connectionId = fixture.Connection.Id;
      var changes = Channel.CreateUnbounded<ToolDraftUpdate>();
      using var watcher = await WatchDrafts(fixture.Session, changes, timeout.Token);
      var selection = new LlmSelection(fixture.Connection, "fixture-model", null, ModelFamily.Other, false);
      UserMessageReceipt receipt = await fixture.Session.SubmitUserMessageAsync(
          new(Guid.NewGuid(), 0, "go", selection));
      Assert.Equal(MessageAcceptance.Accepted, receipt.Acceptance);
      Guid executionId = Assert.IsType<Guid>(receipt.ExecutionId);
      ToolDraftUpdate draft;
      try {
        draft = await ReadDraftAsync(changes, "drafted", timeout.Token);
        Assert.Equal(0, effects);
        if (stop) {
          await fixture.Session.RequestStopAsync(executionId).WaitAsync(timeout.Token);
          Assert.True(changes.Reader.TryPeek(out var next) && next.State == "discarded");
        }
      } finally {
        release.TrySetResult();
      }
      settlement = await fixture.Session.WaitForExecutionFinishAsync(executionId).WaitAsync(TestTimeout);
      ToolDraftUpdate discarded = await ReadDraftAsync(changes, "discarded", timeout.Token);
      Assert.Equal(draft.EntryId, discarded.EntryId);

      Assert.False(changes.Reader.TryRead(out _));
      Assert.Equal(stop ? ExecutionOutcome.Cancelled : ExecutionOutcome.Failed, settlement.Outcome);
      Assert.Equal(stop ? null : "ProviderUnavailable (IOException)", settlement.Error);
      Assert.Equal(0, effects);
      Assert.Single(handler.Requests);
      Assert.Equal(1, handler.StreamDisposals);
      Assert.Empty(StoredTools.Read(database));
      Assert.Empty(ReadProviderResponses(database));
      Assert.Equal(settlement.Error, Assert.Single(StoredExecutions.ReadRequests(database)).Error);
      Assert.All(StoredLlmResponses.Read(database), chunk => Assert.False(chunk.Final));
      SessionSnapshot snapshot = await fixture.Session.ReadSnapshotAsync();
      Assert.False(snapshot.AdmissionFenced);
      Assert.Equal(settlement, snapshot.LastSettlement);
      TranscriptHistoryPage history = await fixture.Session.Transcript.ReadHistoryAsync(
          new(TranscriptHistoryMode.Latest, snapshot.HistoryRevision));
      Assert.Equal(settlement, Assert.Single(history.Settlements));
      Assert.DoesNotContain(history.Entries, entry => entry.Entry.Kind == TranscriptEntryKind.Tool);
    }

    using var reopenedHandler = new ScriptedOpenAIHandler("");
    await using var reopened = new ManagedSession(database, reopenedHandler);
    await reopened.OpenAsync(api, new ToolCatalog([]), sessionId, connectionId);
    Assert.Equal(settlement, (await reopened.Session.ReadSnapshotAsync()).LastSettlement);
    TranscriptHistoryPage reopenedHistory = await reopened.Session.Transcript.ReadHistoryAsync(
        new(TranscriptHistoryMode.Latest, 0));
    Assert.Equal(settlement, Assert.Single(reopenedHistory.Settlements));
  }

  [Fact]
  public async Task ContextMeterEstimatesAcrossAResponsesToolLoopAndThenUsesReportedCounts() {
    const string model = "gpt-6.2-sol";
    using var database = new TemporaryDatabase();
    using var timeout = new CancellationTokenSource(TestTimeout);
    TaskCompletionSource firstBody = Signal(), firstUsage = Signal(), toolStarted = Signal(), toolResult = Signal(), secondBody = Signal();
    const string firstDelta = "prefix ";
    string text = string.Concat(Enumerable.Repeat("some visible output ", 512));
    string arguments = JsonSerializer.Serialize(new { key = string.Concat(Enumerable.Repeat("tool argument ", 650)) });
    // Below the model-facing output cap (ToolOutputLimits.MaxBytes) so the whole result is sent,
    // while still large enough for the context estimate to move visibly when it lands.
    string toolAnswer = string.Concat(Enumerable.Repeat("lookup result text ", 2_000));
    JsonObject call = JsonNode.Parse(ProviderModelStreams.Call)!.AsObject();
    call["arguments"] = arguments;
    string initialStream = ProviderModelStreams.Responses(
        [ProviderModelStreams.Message.Replace("hello", firstDelta + text), ProviderModelStreams.Reasoning, call.ToJsonString()],
        text: text, usage: new { input_tokens = 100, output_tokens = 90 }, model: model,
        firstDelta: firstDelta, toolArgumentDelta: arguments);
    int beforeUsage = initialStream.IndexOf("event: response.completed", StringComparison.Ordinal);
    using var first = new ScriptedOpenAIHandler(initialStream) {
      ReadGate = (System.Text.Encoding.UTF8.GetByteCount(initialStream.AsSpan(0, beforeUsage)), firstUsage.Task)
    };
    using var second = new ScriptedOpenAIHandler(ProviderModelStreams.Responses(
        usage: new { input_tokens = 20_000, output_tokens = 17 }, model: model)
        .Replace("msg_fixture", "msg_next").Replace("resp_fixture", "resp_next"));
    using var firstTransport = new HttpMessageInvoker(first, disposeHandler: false);
    using var secondTransport = new HttpMessageInvoker(second, disposeHandler: false);
    int requests = 0;
    var modelHandler = new ControlledProviderHandler(async (request, cancellation) => {
      switch (Interlocked.Increment(ref requests)) {
        case 1:
          await firstBody.Task.WaitAsync(cancellation);
          return await firstTransport.SendAsync(request, cancellation);
        case 2:
          await secondBody.Task.WaitAsync(cancellation);
          return await secondTransport.SendAsync(request, cancellation);
        case 3:
          await Task.Delay(Timeout.InfiniteTimeSpan, cancellation);
          break;
      }
      throw new InvalidOperationException("Unexpected provider request.");
    });
    var lookup = new TestAgentTool("lookup", async (_, _, cancellation) => {
      toolStarted.TrySetResult();
      await toolResult.Task.WaitAsync(cancellation);
      return new(toolAnswer, []);
    });
    await using var fixture = new ManagedSession(database, modelHandler);
    await fixture.OpenAsync(LlmApi.OpenAIResponses, new ToolCatalog([lookup]));
    var selection = new LlmSelection(fixture.Connection, model, null, ModelFamily.OpenAI, false, 500_000);
    var changes = Channel.CreateBounded<bool>(1);
    using var watcher = await fixture.Session.SubscribeAsync(change => {
      if (change.Status is null) changes.Writer.TryWrite(true);
    }, watched: true, timeout.Token);
    UserMessageReceipt receipt = await fixture.Session.SubmitUserMessageAsync(new(Guid.NewGuid(), 0, "go", selection));
    long prompt = await WaitForEstimateAsync(above: 0);
    firstBody.SetResult();
    // The stream is held without any more deltas. The last batch must still flush, including tool JSON.
    long streamed = await WaitForEstimateAsync(prompt + 2_500);
    Assert.InRange(streamed - prompt, 2_500, 3_300);
    firstUsage.SetResult();
    await toolStarted.Task.WaitAsync(timeout.Token);
    Assert.Equal(new SessionContextUsage(190, SessionContextUsageSource.Measured),
        (await fixture.Session.ReadSnapshotAsync(timeout.Token)).ContextUsage);
    toolResult.SetResult();
    long nextPrompt = await WaitForEstimateAsync(streamed + 4_000);
    Assert.InRange(nextPrompt - streamed, 4_000, 8_000);
    secondBody.SetResult();
    Assert.Equal(ExecutionOutcome.Completed, (await fixture.Session.WaitForExecutionFinishAsync(receipt.ExecutionId!.Value)
        .WaitAsync(timeout.Token)).Outcome);
    Assert.Equal(new SessionContextUsage(20_017, SessionContextUsageSource.Measured),
        (await fixture.Session.ReadSnapshotAsync(timeout.Token)).ContextUsage);

    var next = await fixture.Session.SubmitUserMessageAsync(new(Guid.NewGuid(), 0, "one more", selection));
    Guid stopped = Assert.IsType<Guid>(next.ExecutionId);
    _ = await WaitForEstimateAsync(above: 0);
    await fixture.Session.RequestStopAsync(stopped).WaitAsync(timeout.Token);
    Assert.Equal(ExecutionOutcome.Cancelled, (await fixture.Session.WaitForExecutionFinishAsync(stopped)
        .WaitAsync(timeout.Token)).Outcome);
    Assert.Equal(new SessionContextUsage(20_017, SessionContextUsageSource.LastMeasured),
        (await fixture.Session.ReadSnapshotAsync(timeout.Token)).ContextUsage);

    async Task<long> WaitForEstimateAsync(long above) {
      while (true) {
        SessionSnapshot snapshot = await fixture.Session.ReadSnapshotAsync(timeout.Token);
        if (snapshot.ContextUsage is { Source: SessionContextUsageSource.Estimated } usage && usage.UsedTokens > above)
          return usage.UsedTokens;
        await changes.Reader.ReadAsync(timeout.Token);
      }
    }
  }

  [Fact]
  public async Task ProviderRejectionRemainsInspectableAfterSettlement() {
    using var database = new TemporaryDatabase();
    var handler = new ScriptedOpenAIHandler("""{"detail":"System messages are not allowed"}""",
        "application/json", System.Net.HttpStatusCode.BadRequest);
    await using var fixture = new ManagedSession(database, handler);
    await fixture.OpenAsync(LlmApi.OpenAIResponses, []);
    var selection = new LlmSelection(fixture.Connection, "fixture-model", null, ModelFamily.Other, false);
    UserMessageReceipt receipt = await fixture.Session.SubmitUserMessageAsync(
        new(Guid.NewGuid(), 0, "hello", selection));
    SessionExecutionResult settlement = await fixture.Session.WaitForExecutionFinishAsync(receipt.ExecutionId!.Value)
        .WaitAsync(TestTimeout);

    Assert.Equal(ExecutionOutcome.Failed, settlement.Outcome);
    Assert.Contains("HTTP 400", settlement.Error);
    Assert.Contains("System messages are not allowed", settlement.Error);
    Assert.Equal(settlement, (await fixture.Session.ReadSnapshotAsync()).LastSettlement);
    TranscriptHistoryPage history = await fixture.Session.Transcript.ReadHistoryAsync(new(TranscriptHistoryMode.Latest, 0));
    Assert.Equal(settlement, Assert.Single(history.Settlements));
    Assert.Equal(settlement.Error, Assert.Single(StoredExecutions.ReadRequests(database)).Error);
    Assert.Single(handler.Requests);
  }

  private static void AssertSessionHeader(ScriptedOpenAIRequest request, Guid sessionId) {
    Assert.Equal(sessionId.ToString(), Assert.Single(request.SessionIds!));
    Assert.DoesNotContain(sessionId.ToString(), request.Body, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("session-id", request.Body, StringComparison.OrdinalIgnoreCase);
  }

  private static string ToolResponse(LlmApi api, bool background = false, bool correction = false,
      bool omitCallItemDone = false) {
    string arguments = background ? "{\"key\":\"abc\",\"background\":true}" : "{\"key\":\"abc\"}";
    if (api == LlmApi.OpenAIChatCompletions)
      return ProviderModelStreams.ChatCalls(reasoning: true, arguments: arguments);
    JsonObject call = JsonNode.Parse(ProviderModelStreams.Call)!.AsObject();
    call["arguments"] = arguments;
    string stream = ProviderModelStreams.Responses([ProviderModelStreams.Message,
        ProviderModelStreams.Reasoning.Replace("\"summary\":[]", "\"summary\":[{\"type\":\"summary_text\",\"text\":\"thought\"}]"),
        call.ToJsonString()], itemDone: !omitCallItemDone,
        toolArgumentDelta: correction ? arguments.Replace("abc", "wrong") : arguments);
    JsonObject added = (JsonObject)call.DeepClone();
    added["status"] = "in_progress";
    added["arguments"] = "";
    string delta = "event: response.function_call_arguments.delta";
    stream = stream.Replace(delta, OpenAIStreams.Event("response.output_item.added", new {
      type = "response.output_item.added", sequence_number = 2, output_index = 2, item = added
    }) + delta);
    int firstDone = stream.IndexOf(omitCallItemDone ? "event: response.completed" : "event: response.output_item.done",
        StringComparison.Ordinal);
    stream = stream.Insert(firstDone, OpenAIStreams.Event("response.function_call_arguments.done", new {
      type = "response.function_call_arguments.done", sequence_number = 4, item_id = "fc_fixture", output_index = 2,
      arguments = correction ? arguments.Replace("abc", "wrong") : arguments
    }));
    if (omitCallItemDone)
      stream = stream.Replace("event: response.completed", OpenAIStreams.Event("response.output_item.added", new {
        type = "response.output_item.added", sequence_number = 5, output_index = 3,
        item = new { type = "function_call", id = "fc_stray", status = "in_progress", call_id = "stray", name = "lookup", arguments = "{}" }
      }) + "event: response.completed");
    return stream;
  }

  private static (int, Task) DraftGate(LlmApi api, string stream, Task release, bool argumentsDone = false) {
    string marker = api != LlmApi.OpenAIResponses ? "data: [DONE]"
        : ResponseDraftGateMarker(argumentsDone);
    int offset = stream.IndexOf(marker, StringComparison.Ordinal);
    if (offset < 0 && api == LlmApi.OpenAIChatCompletions) offset = stream.Length;
    Assert.True(offset > 0);
    return (Encoding.UTF8.GetByteCount(stream.AsSpan(0, offset)), release);
  }

  private static string ResponseDraftGateMarker(bool argumentsDone) =>
      argumentsDone ? "event: response.output_item.done" : "event: response.function_call_arguments.done";

  private static Task<IDisposable> WatchDrafts(PrimarySession session, Channel<ToolDraftUpdate> changes, CancellationToken cancellation) =>
      session.SubscribeAsync(change => {
        if (change.ToolDraft is { } draft) changes.Writer.TryWrite(draft);
      }, watched: true, cancellation);

  private static async Task<ToolDraftUpdate> ReadDraftAsync(Channel<ToolDraftUpdate> changes, string state,
      CancellationToken cancellation) {
    ToolDraftUpdate draft;
    do draft = await changes.Reader.ReadAsync(cancellation); while (draft.State != state || draft.ArgumentBytes == 0);
    return draft;
  }

  private static async Task<ToolDraftUpdate> AssertDraftAsync(string state, bool background, UserMessageReceipt receipt,
      Channel<ToolDraftUpdate> changes, PrimarySession session, CancellationToken cancellation, bool correction = false) {
    string arguments = background ? "{\"key\":\"abc\",\"background\":true}" : "{\"key\":\"abc\"}";
    if (correction) arguments = arguments.Replace("abc", "wrong");
    ToolDraftUpdate draft = await ReadDraftAsync(changes, state, cancellation);
    Assert.Equal(receipt.ExecutionId, draft.ExecutionId);
    Assert.Equal("call_fixture", draft.CallId);
    Assert.Equal("lookup", draft.Name);
    Assert.Equal(0, draft.Order);
    Assert.Equal(Encoding.UTF8.GetByteCount(arguments), draft.ArgumentBytes);
    Assert.Equal(arguments, draft.Head + draft.Tail);
    Assert.Equal(background ? "background" : "key", draft.Field);
    TranscriptHistoryPage before = await session.Transcript.ReadHistoryAsync(new(TranscriptHistoryMode.Latest, 0), cancellation);
    Assert.DoesNotContain(before.Entries, entry => entry.Entry.Kind == TranscriptEntryKind.Tool);
    return draft;
  }

  // Inspect the actual SDK request, not a reconstructed context or another serialization pass.
  private static void AssertNativeReplay(LlmApi api, ScriptedOpenAIRequest request, bool followup,
      bool supportsImages, byte[] imageBytes, bool background) {
    using JsonDocument document = JsonDocument.Parse(request.Body);
    JsonElement root = document.RootElement;
    Assert.Equal("fixture-model", root.GetProperty("model").GetString());
    Assert.True(root.GetProperty("stream").GetBoolean());
    Assert.False(root.GetProperty("store").GetBoolean());
    Assert.False(root.TryGetProperty("previous_response_id", out _));
    Assert.False(root.TryGetProperty("conversation", out _));
    Assert.DoesNotContain(ToolDetailsMarker, request.Body);
    JsonElement declaredTool = Assert.Single(root.GetProperty("tools").EnumerateArray());
    Assert.Equal("lookup", (api == LlmApi.OpenAIResponses ? declaredTool : declaredTool.GetProperty("function"))
        .GetProperty("name").GetString());
    if (api == LlmApi.OpenAIResponses) {
      Assert.EndsWith("/responses", request.Uri.AbsolutePath);
      JsonElement[] input = root.GetProperty("input").EnumerateArray().SkipWhile(IsInstruction).ToArray();
      Assert.Equal((followup ? 7 : 5) + (background ? 1 : 0), input.Length);
      Assert.Equal("msg_fixture", input[1].GetProperty("id").GetString());
      Assert.Equal("hello", WireText(input[1].GetProperty("content")));
      Assert.Equal("reasoning", input[2].GetProperty("type").GetString());
      Assert.Equal("rs_fixture", input[2].GetProperty("id").GetString());
      Assert.Equal("private-reasoning", input[2].GetProperty("encrypted_content").GetString());
      Assert.Equal("thought", Assert.Single(input[2].GetProperty("summary").EnumerateArray()).GetProperty("text").GetString());
      JsonElement[] calls = input.Where(item => HasType(item, "function_call")).ToArray();
      JsonElement call = Assert.Single(calls);
      Assert.Equal("fc_fixture", call.GetProperty("id").GetString());
      Assert.Equal("call_fixture", call.GetProperty("call_id").GetString());
      AssertCall(call, background);
      JsonElement result = Assert.Single(input, item => HasType(item, "function_call_output"));
      Assert.Equal("call_fixture", result.GetProperty("call_id").GetString());
      if (background) {
        Assert.Contains("Background tool started", ContentText(result.GetProperty("output")), StringComparison.Ordinal);
        Assert.DoesNotContain(ToolOutputText, result.GetRawText(), StringComparison.Ordinal);
        AssertCompletion(input[5], supportsImages, imageBytes);
      } else {
        AssertToolOutput(result.GetProperty("output"), supportsImages, imageBytes);
      }
      if (followup)
        Assert.Equal("followup", WireText(input[^1].GetProperty("content")));
      Assert.Equal(supportsImages, request.Body.Contains("input_image", StringComparison.Ordinal));
    } else {
      Assert.EndsWith("/chat/completions", request.Uri.AbsolutePath);
      JsonElement[] messages = root.GetProperty("messages").EnumerateArray().SkipWhile(IsInstruction).ToArray();
      int followupCount = background ? 6 : 5;
      Assert.Equal(followup ? followupCount : 4, messages.Length);
      JsonElement assistant = messages[1];
      Assert.Equal("assistant", assistant.GetProperty("role").GetString());
      Assert.Equal("hello", WireText(assistant.GetProperty("content")));
      Assert.Equal("thought", assistant.GetProperty("reasoning_content").GetString());
      JsonElement call = Assert.Single(assistant.GetProperty("tool_calls").EnumerateArray());
      Assert.Equal("call_fixture", call.GetProperty("id").GetString());
      Assert.Equal("function", call.GetProperty("type").GetString());
      AssertCall(call.GetProperty("function"), background);
      JsonElement result = Assert.Single(messages, message => message.GetProperty("role").GetString() == "tool");
      Assert.Equal("call_fixture", result.GetProperty("tool_call_id").GetString());
      string resultText = result.GetProperty("content").GetString()!;
      if (background) {
        Assert.Contains("Background tool started", resultText, StringComparison.Ordinal);
        Assert.DoesNotContain(ToolOutputText, resultText, StringComparison.Ordinal);
        AssertCompletion(messages[3], supportsImages, imageBytes);
        if (followup)
          Assert.Equal("followup", WireText(messages[^1].GetProperty("content")));
        return;
      }
      if (supportsImages) {
        Assert.Equal(ToolOutputText, resultText);
      } else {
        Assert.Contains(ToolOutputText, resultText, StringComparison.Ordinal);
        Assert.Contains(ImageOmissionLabel, resultText, StringComparison.Ordinal);
      }

      if (supportsImages) {
        JsonElement imageMessage = messages[3];
        Assert.Equal("user", imageMessage.GetProperty("role").GetString());
        JsonElement[] imageParts = imageMessage.GetProperty("content").EnumerateArray().ToArray();
        string origin = imageParts[0].GetProperty("text").GetString()!;
        Assert.Contains("call_id=call_fixture", origin, StringComparison.Ordinal);
        Assert.Contains("origin=tool result", origin, StringComparison.Ordinal);
        Assert.Contains("untrusted", origin, StringComparison.Ordinal);
        AssertImageBytes(imageParts[1], imageBytes);
      } else {
        Assert.DoesNotContain("image_url", request.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("TOOL-ORIGIN", request.Body, StringComparison.Ordinal);
        if (followup) {
          Assert.Equal("assistant", messages[3].GetProperty("role").GetString());
          Assert.Equal("followup", WireText(messages[4].GetProperty("content")));
        }
      }
    }
  }

  private static bool IsInstruction(JsonElement message) =>
      message.TryGetProperty("role", out JsonElement role) && role.GetString() is "system" or "developer";

  private static string[] WireInstructions(LlmApi api, ScriptedOpenAIRequest request) {
    using JsonDocument document = JsonDocument.Parse(request.Body);
    return document.RootElement.GetProperty(api == LlmApi.OpenAIResponses ? "input" : "messages")
        .EnumerateArray().TakeWhile(IsInstruction).Select(message => message.GetRawText()).ToArray();
  }

  private static void AssertCompletion(JsonElement message, bool supportsImages, byte[] imageBytes) {
    Assert.Equal("user", message.GetProperty("role").GetString());
    JsonElement content = message.GetProperty("content");
    string text = ContentText(content);
    Assert.StartsWith("Results from background lookup call, ID: call_fixture\n", text, StringComparison.Ordinal);
    Assert.Contains(ToolOutputText, text, StringComparison.Ordinal);
    if (supportsImages) {
      Assert.DoesNotContain(ImageOmissionLabel, text, StringComparison.Ordinal);
      AssertImageBytes(Assert.Single(content.EnumerateArray(), part =>
          HasType(part, "input_image") || HasType(part, "image_url")), imageBytes);
    } else {
      Assert.Contains(ImageOmissionLabel, text, StringComparison.Ordinal);
      Assert.DoesNotContain("image_url", content.GetRawText(), StringComparison.Ordinal);
      Assert.DoesNotContain("base64", text, StringComparison.Ordinal);
    }
  }

  private static void AssertToolOutput(JsonElement output, bool supportsImages, byte[] imageBytes) {
    string text = ContentText(output);
    if (supportsImages) {
      Assert.Equal(ToolOutputText, text);
      JsonElement image = Assert.Single(output.EnumerateArray(), part => HasType(part, "input_image"));
      AssertImageBytes(image, imageBytes);
      return;
    }

    Assert.Contains(ToolOutputText, text, StringComparison.Ordinal);
    Assert.Contains(ImageOmissionLabel, text, StringComparison.Ordinal);
    Assert.DoesNotContain("input_image", output.GetRawText(), StringComparison.Ordinal);
  }

  private static string ContentText(JsonElement content) => content.ValueKind == JsonValueKind.String
      ? content.GetString()!
      : string.Concat(content.EnumerateArray().Where(part => part.TryGetProperty("text", out _))
          .Select(part => part.GetProperty("text").GetString()));

  private static void AssertStoredOutput(string storedResult, byte[] imageBytes) {
    using JsonDocument document = JsonDocument.Parse(storedResult);
    JsonElement output = document.RootElement;
    Assert.Equal(new[] { "text", "images", "details", "isError" }.Order(StringComparer.Ordinal),
        output.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
    Assert.Equal(ToolOutputText, output.GetProperty("text").GetString());
    Assert.False(output.GetProperty("isError").GetBoolean());
    JsonElement image = Assert.Single(output.GetProperty("images").EnumerateArray());
    Assert.Equal(new[] { "hash", "mediaType", "byteLength" }.Order(StringComparer.Ordinal),
        image.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
    Assert.Equal(Convert.ToHexString(SHA256.HashData(imageBytes)).ToLowerInvariant(), image.GetProperty("hash").GetString());
    Assert.Equal("image/png", image.GetProperty("mediaType").GetString());
    Assert.Equal(imageBytes.Length, image.GetProperty("byteLength").GetInt32());
    Assert.Equal(ToolDetailsMarker, output.GetProperty("details").GetProperty("privateMarker").GetString());
  }

  private static void AssertRestoredOutput(ToolOutput? output, byte[] imageBytes) {
    ToolOutput restored = Assert.IsType<ToolOutput>(output);
    Assert.Equal(ToolOutputText, restored.Text);
    Assert.False(restored.IsError);
    Assert.NotNull(restored.Details);
    Assert.Equal(ToolDetailsMarker, restored.Details.Value.GetProperty("privateMarker").GetString());
    DataContent image = Assert.Single(restored.Images);
    Assert.Equal("image/png", image.MediaType);
    Assert.Equal(imageBytes, image.Data.ToArray());
  }

  private static void AssertImageBytes(JsonElement image, byte[] expected) {
    JsonElement imageUrl = image.GetProperty("image_url");
    string dataUrl = imageUrl.ValueKind == JsonValueKind.String
        ? imageUrl.GetString()! : imageUrl.GetProperty("url").GetString()!;
    const string prefix = "data:image/png;base64,";
    Assert.StartsWith(prefix, dataUrl);
    Assert.Equal(expected, Convert.FromBase64String(dataUrl[prefix.Length..]));
  }

  private static byte[] TinyPng() {
    using var bitmap = new SKBitmap(1, 1);
    bitmap.SetPixel(0, 0, SKColors.Blue);
    using var image = SKImage.FromBitmap(bitmap);
    using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
    return encoded.ToArray();
  }

  private static bool HasType(JsonElement item, string type) =>
      item.TryGetProperty("type", out JsonElement value) && value.GetString() == type;

  private static void AssertCall(JsonElement function, bool background) {
    Assert.Equal("lookup", function.GetProperty("name").GetString());
    using JsonDocument arguments = JsonDocument.Parse(function.GetProperty("arguments").GetString()!);
    Assert.Equal("abc", arguments.RootElement.GetProperty("key").GetString());
    Assert.Equal(background, arguments.RootElement.TryGetProperty("background", out JsonElement option));
    if (background)
      Assert.True(option.GetBoolean());
  }

  private static string WireText(JsonElement content) => content.ValueKind == JsonValueKind.String
      ? content.GetString()! : string.Concat(content.EnumerateArray().Select(part => part.GetProperty("text").GetString()));

  private static async Task AssertPublicTextAsync(PrimarySession session, params string[] expected) {
    SessionSnapshot snapshot = await session.ReadSnapshotAsync();
    Assert.False(snapshot.AdmissionFenced);
    Assert.Null(snapshot.UnsettledExecutionId);
    TranscriptHistoryPage history = await session.Transcript.ReadHistoryAsync(
        new(TranscriptHistoryMode.Latest, snapshot.HistoryRevision));
    List<string> textRuns = [];
    foreach (TranscriptEntryDescriptor entry in history.Entries.Where(entry => entry.Entry.Kind == TranscriptEntryKind.Text)) {
      MessageContentPage page = await session.Transcript.ReadContentAsync(entry.Text!.From);
      var text = new System.Text.StringBuilder(page.Text);
      while (page.HasMore) {
        page = await session.Transcript.ReadContentAsync(page.Next);
        text.Append(page.Text);
      }
      textRuns.Add(text.ToString());
    }

    Assert.Equal(expected, textRuns);
    Assert.All(textRuns, text => {
      Assert.DoesNotContain("thought", text);
      Assert.DoesNotContain("private-reasoning", text);
    });
  }

  private static Dictionary<Guid, LlmProviderResponse> AssertCompletedContexts(
      TemporaryDatabase database, int count, Guid connectionId, LlmApi api) {
    List<StoredRequest> requests = StoredExecutions.ReadRequests(database);
    Dictionary<Guid, LlmProviderResponse> contexts = ReadProviderResponses(database);
    Assert.Equal(count, requests.Count);
    Assert.Equal(count, contexts.Count);
    Assert.All(requests, request => {
      Assert.Equal(ExecutionOutcome.Completed, request.Outcome);
      Assert.True(contexts.ContainsKey(request.RequestId));
      LlmProviderScope scope = contexts[request.RequestId].Scope;
      Assert.Equal(connectionId, scope.ConnectionId);
      Assert.Equal(api, scope.Api);
      Assert.Equal("fixture-model", scope.ModelId);
    });
    return contexts;
  }

  private static Dictionary<Guid, LlmProviderResponse> ReadProviderResponses(TemporaryDatabase database) {
    using SqliteConnection connection = database.OpenReadOnlyConnection();
    using SqliteCommand command = connection.CreateCommand();
    command.CommandText = "SELECT request_id, response_json FROM execution_provider_context;";
    using SqliteDataReader reader = command.ExecuteReader();
    var responses = new Dictionary<Guid, LlmProviderResponse>();
    while (reader.Read())
      responses.Add(Guid.Parse(reader.GetString(0)), JsonSerializer.Deserialize<LlmProviderResponse>(reader.GetString(1))!);
    return responses;
  }

  // Preserve native values but vary property ordering in the server's completed output.
  private static string ReorderCompletedOutput(string stream) {
    return string.Join("\n\n", stream.Split("\n\n", StringSplitOptions.RemoveEmptyEntries).Select(@event => {
      string[] lines = @event.Split('\n');
      if (lines[0] != "event: response.completed")
        return @event;
      JsonObject completed = JsonNode.Parse(lines[1][6..])!.AsObject();
      JsonArray output = completed["response"]!["output"]!.AsArray();
      for (int i = 0; i < output.Count; i++) {
        JsonObject item = output[i]!.AsObject();
        var reordered = new JsonObject();
        foreach (var property in item.Reverse())
          reordered.Add(property.Key, property.Value?.DeepClone());
        output[i] = reordered;
      }
      return lines[0] + "\ndata: " + completed.ToJsonString();
    })) + "\n\n";
  }

  private sealed class LoopSocket : WebSocket {
    private readonly Queue<byte[]> frames;
    private readonly bool closeWhenExhausted;
    private int offset;
    internal List<JsonObject> Requests { get; } = [];
    internal bool Disposed { get; private set; }

    internal LoopSocket(string[] streams, bool closeWhenExhausted = false) {
      this.closeWhenExhausted = closeWhenExhausted;
      frames = new(streams.SelectMany(stream => stream.Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
          .Select(@event => @event.Split('\n').Single(line => line.StartsWith("data: ", StringComparison.Ordinal))[6..])
          .Select(json => Encoding.UTF8.GetBytes(json)));
    }

    public override WebSocketCloseStatus? CloseStatus => null;
    public override string? CloseStatusDescription => null;
    public override WebSocketState State => Disposed ? WebSocketState.Closed : WebSocketState.Open;
    public override string? SubProtocol => null;
    public override void Abort() => Dispose();
    public override void Dispose() => Disposed = true;
    public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => throw new NotSupportedException();
    public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => throw new NotSupportedException();
    public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken) {
      cancellationToken.ThrowIfCancellationRequested();
      Assert.Equal(WebSocketMessageType.Text, messageType);
      Assert.True(endOfMessage);
      Requests.Add(JsonNode.Parse(buffer.AsSpan())!.AsObject());
      return Task.CompletedTask;
    }
    public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) {
      cancellationToken.ThrowIfCancellationRequested();
      if (frames.Count == 0) {
        if (closeWhenExhausted)
          return Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true));
        throw new WebSocketException("Synthetic interrupted response");
      }
      byte[] frame = frames.Peek();
      int count = Math.Min(buffer.Count, frame.Length - offset);
      frame.AsSpan(offset, count).CopyTo(buffer.AsSpan());
      offset += count;
      bool end = offset == frame.Length;
      if (end) { frames.Dequeue(); offset = 0; }
      return Task.FromResult(new WebSocketReceiveResult(count, WebSocketMessageType.Text, end));
    }
  }

  // Own the routing invokers, but borrow handlers; the test's using declarations own those handlers.
  private sealed class ChatGptLoopTransport(FakeOpenAIIdentity identity, ScriptedOpenAIHandler first,
      ScriptedOpenAIHandler done) : HttpMessageHandler {
    private readonly HttpMessageInvoker auth = new(identity, false);
    private readonly HttpMessageInvoker initial = new(first, false);
    private readonly HttpMessageInvoker subsequent = new(done, false);
    internal List<ScriptedOpenAIRequest> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
      Uri uri = request.RequestUri!;
      if (uri.GetLeftPart(UriPartial.Authority) == FakeOpenAIIdentity.Issuer.GetLeftPart(UriPartial.Authority))
        return await auth.SendAsync(request, cancellationToken);
      Assert.Equal("https://api.openai.com", uri.GetLeftPart(UriPartial.Authority));
      Assert.Equal("/v1/responses", uri.AbsolutePath);
      string body = await request.Content!.ReadAsStringAsync(cancellationToken);
      Requests.Add(new(request.Method, uri, request.Headers.Authorization?.ToString(), body));
      return await (Requests.Count == 1 ? initial : subsequent).SendAsync(request, cancellationToken);
    }

    protected override void Dispose(bool disposing) {
      if (disposing) {
        auth.Dispose();
        initial.Dispose();
        subsequent.Dispose();
      }
      base.Dispose(disposing);
    }
  }

  // Own the real bridge and execution together so even assertion failures join work before SQLite closes.
  private sealed class ManagedSession : IAsyncDisposable {
    private readonly UrdStore store;
    private readonly ProviderManager manager;
    private readonly string workingDirectory;
    private IChatClient? client;
    private PrimarySession? session;

    internal ManagedSession(TemporaryDatabase database, HttpMessageHandler handler) {
      store = UrdStore.Open(database.Path);
      workingDirectory = Path.GetDirectoryName(database.Path)!;
      manager = new(store, () => new Uri("http://127.0.0.1:1234/auth/callback"), handler);
    }

    internal PrimarySession Session => session!;
    internal ProviderConnection Connection { get; private set; } = null!;

    internal Task OpenAsync(LlmApi api, IReadOnlyList<AIFunction> tools,
        Guid? sessionId = null, Guid? connectionId = null) =>
        OpenAsync(api, TestAgentTool.CreateToolCatalog(tools), sessionId, connectionId);

    internal async Task OpenAsync(LlmApi api, ToolCatalog tools,
        Guid? sessionId = null, Guid? connectionId = null) {
      Connection = connectionId is { } savedConnection
          ? await manager.GetAsync(savedConnection)
          : await manager.AddCustomAsync(new("managed execution", api, new Uri("https://fixture.invalid/v1/"), AuthenticationMethod.None));
      client = manager.CreateChatClient(Connection);
      SessionRecord record = sessionId is { } savedSession
          ? Assert.IsType<SessionRecord>(store.Sessions.Read(savedSession))
          : store.Sessions.CreatePrimary(Guid.NewGuid(), workingDirectory);
      session = new(record, store, _ => client, tools);
    }

    internal ToolOutput? ReadOutput(Guid requestId, string callId) =>
        store.Tools.ReadOutput(Session.Id, requestId, callId);

    internal async Task SendAsync(string text, ExecutionOutcome outcome = ExecutionOutcome.Completed,
        bool supportsImages = false) {
      var selection = new LlmSelection(Connection, "fixture-model", null, ModelFamily.Other, supportsImages);
      UserMessageReceipt receipt = await Session.SubmitUserMessageAsync(new(Guid.NewGuid(), 0, text, selection));
      Assert.Equal(MessageAcceptance.Accepted, receipt.Acceptance);
      Guid executionId = Assert.IsType<Guid>(receipt.ExecutionId);
      Assert.Equal(outcome, (await Session.WaitForExecutionFinishAsync(executionId).WaitAsync(TestTimeout)).Outcome);
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
    }
  }
}

#pragma warning restore MEAI001
