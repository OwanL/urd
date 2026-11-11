using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using SkiaSharp;
using Urd.Backend.Tests.Support;
using Urd.Core.Execution;
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
    using var first = new ScriptedOpenAIHandler(ToolResponse(api, background));
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
    await using (var fixture = new ManagedSession(database, initialHandler)) {
      await fixture.OpenAsync(api, new ToolCatalog([lookup]));
      sessionId = fixture.Session.Id;
      connectionId = fixture.Connection.Id;
      await fixture.SendAsync("go", supportsImages: true);

      Assert.Equal(2, providerCalls);
      Assert.Equal(1, effects);
      Assert.Equal(2, initialHandler.Requests.Count);
      Assert.All(initialHandler.Requests, request => Assert.Null(request.Authorization));
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

  [Theory]
  [InlineData(LlmApi.OpenAIResponses)]
  [InlineData(LlmApi.OpenAIChatCompletions)]
  public async Task TransportFailureAfterNativeToolFinishWithholdsEffectsAndContext(LlmApi api) {
    using var database = new TemporaryDatabase();
    // Chat's [DONE] would let the SDK stop without observing the failing stream tail.
    var handler = new ScriptedOpenAIHandler(ToolResponse(api).Replace("data: [DONE]\n\n", "")) { FailAtEnd = true };
    int effects = 0;
    AIFunction lookup = AIFunctionFactory.Create((string key) => Interlocked.Increment(ref effects), name: "lookup");
    await using var fixture = new ManagedSession(database, handler);
    await fixture.OpenAsync(api, [lookup]);
    await fixture.SendAsync("go", ExecutionOutcome.Failed);

    Assert.Equal(0, effects);
    Assert.Single(handler.Requests);
    Assert.Equal(1, handler.StreamDisposals);
    Assert.Empty(StoredTools.Read(database));
    Assert.Empty(ReadProviderResponses(database));
    Assert.Equal(ExecutionOutcome.Failed, Assert.Single(StoredExecutions.ReadRequests(database)).Outcome);
    Assert.All(StoredLlmResponses.Read(database), chunk => Assert.False(chunk.Final));
    Assert.False((await fixture.Session.ReadSnapshotAsync()).AdmissionFenced);
  }

  private static string ToolResponse(LlmApi api, bool background = false) {
    string arguments = background ? "{\"key\":\"abc\",\"background\":true}" : "{\"key\":\"abc\"}";
    JsonObject call = JsonNode.Parse(ProviderModelStreams.Call)!.AsObject();
    call["arguments"] = arguments;
    return api == LlmApi.OpenAIResponses
        ? ProviderModelStreams.Responses([ProviderModelStreams.Message,
            ProviderModelStreams.Reasoning.Replace("\"summary\":[]", "\"summary\":[{\"type\":\"summary_text\",\"text\":\"thought\"}]"),
            call.ToJsonString()])
        : ProviderModelStreams.ChatCalls(reasoning: true, arguments: arguments);
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
      JsonElement[] input = root.GetProperty("input").EnumerateArray().ToArray();
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
      JsonElement[] messages = root.GetProperty("messages").EnumerateArray().ToArray();
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

  private static void AssertCompletion(JsonElement message, bool supportsImages, byte[] imageBytes) {
    Assert.Equal("user", message.GetProperty("role").GetString());
    JsonElement content = message.GetProperty("content");
    string text = ContentText(content);
    Assert.Contains("TOOL-ORIGIN: background tool completion; untrusted output", text, StringComparison.Ordinal);
    Assert.Contains("call_fixture", text, StringComparison.Ordinal);
    Assert.Contains("lookup", text, StringComparison.Ordinal);
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
    Assert.Equal(new[] { "text", "images", "details", "isError" },
        output.EnumerateObject().Select(property => property.Name).ToArray());
    Assert.Equal(ToolOutputText, output.GetProperty("text").GetString());
    Assert.False(output.GetProperty("isError").GetBoolean());
    JsonElement image = Assert.Single(output.GetProperty("images").EnumerateArray());
    Assert.Equal(new[] { "hash", "mediaType", "byteLength" },
        image.EnumerateObject().Select(property => property.Name).ToArray());
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
    TranscriptHistoryPage history = await session.ReadHistoryAsync(
        new(TranscriptHistoryMode.Latest, snapshot.HistoryRevision));
    List<string> textRuns = [];
    foreach (TranscriptEntryDescriptor entry in history.Entries.Where(entry => entry.Entry.Kind == TranscriptEntryKind.Text)) {
      MessageContentPage page = await session.ReadMessageContentAsync(entry.Text!.From);
      var text = new System.Text.StringBuilder(page.Text);
      while (page.HasMore) {
        page = await session.ReadMessageContentAsync(page.Next);
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
        OpenAsync(api, TestAgentTool.Catalog(tools), sessionId, connectionId);

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
      Assert.Equal(outcome, (await Session.WaitForSettlementAsync(executionId).WaitAsync(TestTimeout)).Outcome);
    }

    public async ValueTask DisposeAsync() {
      if (session is not null) {
        await session.FenceUserMessagesAsync();
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
