using System.ClientModel.Primitives;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenAI.Chat;
using OpenAI.Responses;
using Urd.Core.Providers;
using Xunit;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

#pragma warning disable OPENAI001 // Exercise the pinned SDK's real native persistence APIs offline.

namespace Urd.Backend.Tests.Providers;

public sealed class LlmProviderContextTests {
  private static readonly LlmProviderScope Responses = new(Guid.NewGuid(), 3, LlmApi.OpenAIResponses, "requested-model");
  private static readonly LlmProviderScope Chat = Responses with { Api = LlmApi.OpenAIChatCompletions };

  [Fact]
  public void DurableResponsesRoundtripPreservesOrderedNativeItemsAndUnknownFields() {
    ResponseItem[] items = [
      Item("""{"type":"reasoning","id":"rs-1","status":"completed","summary":[{"type":"summary_text","text":"Thinking"}],"encrypted_content":"opaque-reasoning"}"""),
      Item("""{"type":"message","id":"msg-1","status":"completed","role":"assistant","content":[{"type":"output_text","text":"Before","annotations":[]}],"provider_extension":{"keep":17}}"""),
      Item("""{"type":"function_call","id":"fc-1","call_id":"call-1","status":"completed","name":"lookup","arguments":"{\"value\":1.20}"}"""),
      Item("""{"type":"additional_tools","id":"append-1","tools":[{"type":"function","name":"later","parameters":{"type":"object"}}]}"""),
      Item("""{"type":"message","role":"developer","content":[{"type":"input_text","text":"Appended definition"}]}"""),
      Item("""{"type":"message","id":"msg-2","status":"completed","role":"assistant","content":[{"type":"output_text","text":"After","annotations":[]}]}""")
    ];
    LlmProviderResponse captured = LlmProviderContext.CaptureResponses(Responses, items);

    LlmProviderResponse durable = Reload(captured);
    ChatMessage restored = LlmProviderContext.Restore(durable);
    ChatMessage replay = Assert.Single(LlmProviderContext.Prepare([restored], Responses));

    Assert.Equal(captured, durable);
    Assert.Equal("BeforeAfter", restored.Text);
    Assert.Equal(durable, restored.RawRepresentation);
    Assert.Null(replay.RawRepresentation);
    Assert.Equal(items.Length, replay.Contents.Count);
    for (int index = 0; index < items.Length; index++) {
      ResponseItem raw = Assert.IsAssignableFrom<ResponseItem>(replay.Contents[index].RawRepresentation);
      AssertJsonEqual(NativeJson(items[index]), NativeJson(raw));
    }
    Assert.Equal("call-1", Assert.Single(restored.Contents.OfType<FunctionCallContent>()).CallId);
    Assert.Equal("opaque-reasoning", Assert.Single(restored.Contents.OfType<TextReasoningContent>()).ProtectedData);
  }

  [Theory]
  [InlineData("connection")]
  [InlineData("generation")]
  [InlineData("api")]
  [InlineData("model")]
  public void ChangedScopeKeepsVisibleTextAndCallsButOmitsPrivateReasoning(string change) {
    LlmProviderResponse captured = LlmProviderContext.CaptureResponses(Responses, [
      Item("""{"type":"message","id":"msg-1","role":"assistant","content":[{"type":"output_text","text":"Visible","annotations":[]}]}"""),
      Item("""{"type":"reasoning","id":"rs-1","summary":[{"type":"summary_text","text":"Private"}],"encrypted_content":"opaque"}"""),
      Item("""{"type":"function_call","id":"fc-1","call_id":"call-1","name":"lookup","arguments":"{\"nested\":{\"number\":1.20},\"list\":[true,null]}"}""")
    ]);
    LlmProviderScope target = change switch {
      "connection" => Responses with { ConnectionId = Guid.NewGuid() },
      "generation" => Responses with { Generation = Responses.Generation + 1 },
      "model" => Responses with { ModelId = "another-requested-model" },
      _ => Chat
    };

    ChatMessage portable = Assert.Single(LlmProviderContext.Prepare([LlmProviderContext.Restore(Reload(captured))], target));

    Assert.Equal("Visible", portable.Text);
    Assert.Null(portable.RawRepresentation);
    Assert.All(portable.Contents, content => Assert.Null(content.RawRepresentation));
    Assert.DoesNotContain(portable.Contents, content => content is TextReasoningContent);
    FunctionCallContent call = Assert.Single(portable.Contents.OfType<FunctionCallContent>());
    Assert.Equal("call-1", call.CallId);
    Assert.Equal("lookup", call.Name);
    AssertJsonEqual("""{"nested":{"number":1.20},"list":[true,null]}""", JsonSerializer.Serialize(call.Arguments));
  }

  [Theory]
  [InlineData(LlmApi.OpenAIResponses)]
  [InlineData(LlmApi.OpenAIChatCompletions)]
  public void RequestedModelSwitchOmitsReasoningEvenWhenBothResolveToTheSameModel(LlmApi api) {
    LlmProviderScope source = Responses with { Api = api };
    var portable = new ChatMessage(ChatRole.Assistant, new List<AIContent> {
      new TextContent("Visible"),
      new TextReasoningContent("Private"),
      new FunctionCallContent("call-1", "lookup", new Dictionary<string, object?> { ["value"] = 1 })
    });
    LlmProviderResponse captured = api == LlmApi.OpenAIChatCompletions
        ? LlmProviderContext.CaptureChat(source, portable)
        : LlmProviderContext.CaptureResponses(source, [
          Item("""{"type":"message","role":"assistant","content":[{"type":"output_text","text":"Visible","annotations":[]}]}"""),
          Item("""{"type":"reasoning","summary":[{"type":"summary_text","text":"Private"}],"encrypted_content":"opaque"}"""),
          Item("""{"type":"function_call","call_id":"call-1","name":"lookup","arguments":"{\"value\":1}"}""")
        ]);
    var first = new ChatResponseUpdate { ModelId = "same-resolved-model" };
    LlmProviderContext.SetResponse(first, captured);
    var next = new ChatResponseUpdate { ModelId = first.ModelId };
    LlmProviderScope target = source with { ModelId = "another-requested-model" };

    LlmProviderResponse durable = Reload(LlmProviderContext.GetResponse(first)!);
    ChatMessage restored = LlmProviderContext.Restore(durable);
    ChatMessage replay = Assert.Single(LlmProviderContext.Prepare([restored], target));

    Assert.Equal(first.ModelId, next.ModelId);
    Assert.NotEqual(target.ModelId, durable.Scope.ModelId);
    Assert.Equal(source.ModelId, durable.Scope.ModelId);
    Assert.Equal("Visible", replay.Text);
    Assert.Single(replay.Contents.OfType<FunctionCallContent>());
    Assert.DoesNotContain(replay.Contents, content => content is TextReasoningContent);
    Assert.Null(replay.RawRepresentation);
    Assert.All(replay.Contents, content => Assert.Null(content.RawRepresentation));
  }

  [Theory]
  [InlineData(LlmApi.OpenAIResponses)]
  [InlineData(LlmApi.OpenAIChatCompletions)]
  public void PlanUrdNamespaceIsPreservedNativelyAndTransfersAsTheSamePortableCall(LlmApi api) {
    ResponseItem native = Item("""{"type":"function_call","id":"fc-1","call_id":"call-1","namespace":"urd","name":"lookup","arguments":"{\"value\":1.20}"}""");
    LlmProviderResponse response = Reload(LlmProviderContext.CaptureResponses(Responses, [native]));
    ChatMessage restored = LlmProviderContext.Restore(response);
    ChatMessage sameScope = Assert.Single(LlmProviderContext.Prepare([restored], Responses));
    ResponseItem replay = Assert.IsAssignableFrom<ResponseItem>(Assert.Single(sameScope.Contents).RawRepresentation);
    AssertJsonEqual(NativeJson(native), NativeJson(replay));

    ChatMessage changed = Assert.Single(LlmProviderContext.Prepare([restored], Responses with { Api = api, ModelId = "another-model" }));
    FunctionCallContent call = Assert.IsType<FunctionCallContent>(Assert.Single(changed.Contents));
    Assert.Equal("call-1", call.CallId);
    Assert.Equal("lookup", call.Name);
    AssertJsonEqual("{\"value\":1.20}", JsonSerializer.Serialize(call.Arguments));
    Assert.Null(call.RawRepresentation);
    Assert.Null(changed.RawRepresentation);
  }

  [Fact]
  public void RequestedModelIdsAreRequiredBoundedAndNeverTrimmedOrResolved() {
    foreach (string model in new[] { "", " ", "model\n", new string('a', 257) }) {
      LlmProviderScope invalid = Responses with { ModelId = model };
      Assert.Throws<InvalidDataException>(() => LlmProviderContext.CaptureResponses(invalid, []));
      Assert.Throws<InvalidDataException>(() => LlmProviderContext.CaptureChat(invalid with { Api = LlmApi.OpenAIChatCompletions }, new ChatMessage(ChatRole.Assistant, "text")));
      Assert.Throws<InvalidDataException>(() => LlmProviderContext.Prepare([], invalid));
      Assert.Throws<InvalidDataException>(() => LlmProviderContext.Restore(new LlmProviderResponse(invalid, "{\"version\":1,\"items\":[]}")));
    }
    LlmProviderScope maximum = Responses with { ModelId = new string('a', 256) };
    Assert.Equal(maximum, Reload(LlmProviderContext.CaptureResponses(maximum, [])).Scope);
    LlmProviderScope exact = Responses with { ModelId = " Requested-Alias " };
    Assert.Equal(exact.ModelId, Reload(LlmProviderContext.CaptureResponses(exact, [])).Scope.ModelId);
  }

  [Theory]
  [InlineData("""{"type":"additional_tools","tools":[]}""")]
  [InlineData("""{"type":"future_visible_item","payload":{"position":1}}""")]
  [InlineData("""{"type":"message","role":"developer","content":[{"type":"input_text","text":"Definition"}]}""")]
  [InlineData("""{"type":"message","role":"assistant","content":[{"type":"output_image","image_url":"https://invalid.example/image"}]}""")]
  [InlineData("""{"type":"message","role":"assistant","content":[{"type":"output_text","text":"Visible","annotations":[{"type":"url_citation","start_index":0,"end_index":7,"title":"fixture","url":"https://fixture.invalid/"}]}]}""")]
  [InlineData("""{"type":"function_call","call_id":"call-1","name":"lookup","arguments":"{}","namespace":"private"}""")]
  public void NonportableResponsesNeverSilentlyDisappearOnScopeChange(string json) {
    LlmProviderResponse response = LlmProviderContext.CaptureResponses(Responses, [Item(json)]);
    ChatMessage restored = LlmProviderContext.Restore(Reload(response));

    Assert.Single(LlmProviderContext.Prepare([restored], Responses));
    Assert.Throws<NotSupportedException>(() => LlmProviderContext.Prepare([restored], Chat));
  }

  [Theory]
  [InlineData("reasoning_content")]
  [InlineData("reasoning")]
  public void ChatReasoningRetainsItsWireFieldOnlyWithinTheSameScope(string field) {
    var message = new ChatMessage(ChatRole.Assistant, "answer");
    message.Contents.Add(new TextReasoningContent("private thought"));
    LlmProviderResponse response = Reload(LlmProviderContext.CaptureChat(Chat, message, field));
    ChatMessage restored = LlmProviderContext.Restore(response);
    Assert.Equal("private thought", Assert.Single(restored.Contents.OfType<TextReasoningContent>()).Text);
    ChatMessage same = Assert.Single(LlmProviderContext.Prepare([restored], Chat));
    using var wire = JsonDocument.Parse(ModelReaderWriter.Write(Assert.IsType<AssistantChatMessage>(same.RawRepresentation)));
    Assert.Equal("private thought", wire.RootElement.GetProperty(field).GetString());
    ChatMessage changed = Assert.Single(LlmProviderContext.Prepare([restored], Chat with { ModelId = "different-model" }));
    Assert.Equal("answer", changed.Text);
    Assert.Empty(changed.Contents.OfType<TextReasoningContent>());
    Assert.Null(changed.RawRepresentation);
  }

  [Fact]
  public void NamedSpeakersCannotSilentlyBecomeAnonymous() {
    var message = new ChatMessage(ChatRole.Assistant, "hello") { AuthorName = "reviewer" };
    Assert.Throws<NotSupportedException>(() => LlmProviderContext.Prepare([message], Responses));
    Assert.Throws<NotSupportedException>(() => LlmProviderContext.Prepare([message], Chat));
    Assert.Throws<NotSupportedException>(() => LlmProviderContext.CaptureChat(Chat, message));
  }

  [Fact]
  public void CaptureFreezesSdkObjectsAndReplayDoesNotShareMutableObjects() {
    var item = new FunctionCallResponseItem("call-1", "lookup", BinaryData.FromString("{\"value\":1}")) { Id = "fc-1" };
    var items = new List<ResponseItem> { item };
    LlmProviderResponse response = LlmProviderContext.CaptureResponses(Responses, items);
    string frozen = response.Json;
    item.FunctionName = "changed";
    item.Id = "changed-id";
    items.Clear();

    ChatMessage first = Assert.Single(LlmProviderContext.Prepare([LlmProviderContext.Restore(response)], Responses));
    var raw = Assert.IsType<FunctionCallResponseItem>(Assert.Single(first.Contents).RawRepresentation);
    raw.FunctionName = "replay-mutated";
    ChatMessage second = Assert.Single(LlmProviderContext.Prepare([LlmProviderContext.Restore(response)], Responses));
    var independent = Assert.IsType<FunctionCallResponseItem>(Assert.Single(second.Contents).RawRepresentation);

    Assert.Equal(frozen, response.Json);
    Assert.Equal("lookup", independent.FunctionName);
    Assert.Equal("fc-1", independent.Id);
  }

  [Fact]
  public void DurableChatRoundtripRetainsAssistantNativeReasoningAndCalls() {
    var arguments = new Dictionary<string, object?> { ["count"] = 2 };
    var message = new ChatMessage(ChatRole.Assistant, new List<AIContent> {
      new TextContent("Hello"),
      new TextReasoningContent("Private thought"),
      new FunctionCallContent("call-1", "lookup", arguments)
    });
    LlmProviderResponse captured = LlmProviderContext.CaptureChat(Chat, message);
    arguments["count"] = 99;
    message.Contents.Clear();
    ChatMessage restored = LlmProviderContext.Restore(Reload(captured));

    ChatMessage replay = Assert.Single(LlmProviderContext.Prepare([restored], Chat));
    var raw = Assert.IsType<AssistantChatMessage>(replay.RawRepresentation);
    using JsonDocument document = JsonDocument.Parse(ModelReaderWriter.Write(raw));
    JsonElement native = document.RootElement;
    Assert.Equal("Private thought", native.GetProperty("reasoning_content").GetString());
    JsonElement call = Assert.Single(native.GetProperty("tool_calls").EnumerateArray());
    Assert.Equal("call-1", call.GetProperty("id").GetString());
    AssertJsonEqual("{\"count\":2}", call.GetProperty("function").GetProperty("arguments").GetString()!);
    Assert.Equal("Hello", restored.Text);
    Assert.Equal("Private thought", Assert.Single(restored.Contents.OfType<TextReasoningContent>()).Text);

    ChatMessage portable = Assert.Single(LlmProviderContext.Prepare([restored], Responses));
    Assert.Equal("Hello", portable.Text);
    Assert.Single(portable.Contents.OfType<FunctionCallContent>());
    Assert.DoesNotContain(portable.Contents, content => content is TextReasoningContent);
    Assert.Null(portable.RawRepresentation);
  }

  [Fact]
  public void NativeChatAssistantMessageIsFrozenThroughOfficialPersistence() {
    var raw = ModelReaderWriter.Read<AssistantChatMessage>(BinaryData.FromString("""
        {"role":"assistant","content":"Hello","reasoning_content":"Private","tool_calls":[
          {"id":"call-1","type":"function","function":{"name":"lookup","arguments":"{}"}}]}
        """))!;
    LlmProviderResponse response = LlmProviderContext.CaptureChat(Chat, new ChatMessage(ChatRole.Assistant, "Hello") { RawRepresentation = raw });
    raw.ToolCalls.Clear();

    ChatMessage replay = Assert.Single(LlmProviderContext.Prepare([LlmProviderContext.Restore(Reload(response))], Chat));
    var restored = Assert.IsType<AssistantChatMessage>(replay.RawRepresentation);
    Assert.Single(restored.ToolCalls);
    using JsonDocument document = JsonDocument.Parse(ModelReaderWriter.Write(restored));
    Assert.Equal("Private", document.RootElement.GetProperty("reasoning_content").GetString());
  }

  [Fact]
  public void ResponseMetadataDoesNotReplaceNativeStreamingEvidence() {
    object native = new();
    var update = new ChatResponseUpdate { RawRepresentation = native };
    Assert.Null(LlmProviderContext.GetResponse(update));
    LlmProviderResponse response = LlmProviderContext.CaptureResponses(Responses, []);

    LlmProviderContext.SetResponse(update, response);

    Assert.Same(response, LlmProviderContext.GetResponse(update));
    Assert.Same(native, update.RawRepresentation);
  }

  [Theory]
  [InlineData("")]
  [InlineData("not-json")]
  [InlineData("{}")]
  [InlineData("{\"version\":2,\"items\":[]}")]
  [InlineData("{\"version\":1,\"items\":null}")]
  [InlineData("{\"version\":1,\"items\":[null]}")]
  [InlineData("{\"version\":1,\"items\":[{}]}")]
  [InlineData("""{"version":1,"items":[{"type":"function_call","call_id":"call-1","name":"lookup","arguments":"[]"}]}""")]
  [InlineData("""{"version":1,"items":[{"type":"function_call","call_id":"","name":"lookup","arguments":"{}"}]}""")]
  public void MalformedOrUnknownVersionPayloadFailsWithoutEchoingIt(string json) {
    var response = new LlmProviderResponse(Responses, json);
    InvalidDataException error = Assert.Throws<InvalidDataException>(() => LlmProviderContext.Restore(response));
    Assert.Equal("Invalid provider context.", error.Message);
    Assert.Null(error.InnerException);
  }

  [Fact]
  public void ApiMismatchAndUnsupportedGenericContentFailInsteadOfBeingDropped() {
    Assert.Throws<InvalidDataException>(() => LlmProviderContext.CaptureResponses(Chat, []));
    Assert.Throws<InvalidDataException>(() => LlmProviderContext.CaptureChat(Responses, new ChatMessage(ChatRole.Assistant, "hello")));
    var image = new ChatMessage(ChatRole.Assistant, new List<AIContent> { new UriContent("https://invalid.example/image.png", "image/png") });
    Assert.Throws<NotSupportedException>(() => LlmProviderContext.CaptureChat(Chat, image));
    Assert.Throws<NotSupportedException>(() => LlmProviderContext.Prepare([image], Responses));
    var result = new ChatMessage(ChatRole.Assistant, new List<AIContent> { new FunctionResultContent("call-1", "result") });
    Assert.Throws<InvalidDataException>(() => LlmProviderContext.CaptureChat(Chat, result));
    var unpairedToolText = new ChatMessage(ChatRole.Tool, "would silently disappear");
    Assert.Throws<InvalidDataException>(() => LlmProviderContext.Prepare([unpairedToolText], Chat));
  }

  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public void MultimodalToolResultsFailInsteadOfBypassingContentValidation(bool list) {
    AIContent image = new UriContent("https://invalid.example/image.png", "image/png");
    object result = list ? new[] { image } : image;
    var message = new ChatMessage(ChatRole.Tool, new List<AIContent> { new FunctionResultContent("call-1", result) });

    Assert.Throws<NotSupportedException>(() => LlmProviderContext.Prepare([message], Responses));
    Assert.Throws<NotSupportedException>(() => LlmProviderContext.Prepare([message], Chat));
  }

  [Theory]
  [InlineData("""{"version":1,"message":{"role":"user","content":"wrong role"}}""")]
  [InlineData("""{"version":1,"message":{"role":"assistant","content":42}}""")]
  [InlineData("""{"version":1,"message":{"role":"assistant","reasoning_content":{}}}""")]
  [InlineData("""{"version":1,"message":{"role":"assistant","tool_calls":[{"type":"function","id":"call-1","function":{"name":"lookup","arguments":"not-json"}}]}}""")]
  [InlineData("""{"version":1,"message":{"role":"assistant","audio":{"id":"hosted"}}}""")]
  public void MalformedAndUnsupportedChatPayloadsFail(string json) {
    Assert.Throws<InvalidDataException>(() => LlmProviderContext.Restore(new LlmProviderResponse(Chat, json)));
  }

  [Fact]
  public void WholeEnvelopeByteLimitFailsRatherThanTruncatingNativeContent() {
    string text = new('a', LlmProviderContext.MaximumBytes);
    Assert.Throws<InvalidDataException>(() => LlmProviderContext.CaptureResponses(Responses, [ResponseItem.CreateAssistantMessageItem(text)]));
    Assert.Throws<InvalidDataException>(() => LlmProviderContext.CaptureChat(Chat, new ChatMessage(ChatRole.Assistant, text)));
    // Fewer than MaximumBytes characters, but too many UTF-8 bytes.
    string json = "{\"version\":1,\"items\":[],\"padding\":\"" + new string('é', LlmProviderContext.MaximumBytes / 2) + "\"}";
    Assert.True(json.Length < LlmProviderContext.MaximumBytes);
    Assert.True(Encoding.UTF8.GetByteCount(json) > LlmProviderContext.MaximumBytes);
    Assert.Throws<InvalidDataException>(() => LlmProviderContext.Restore(new LlmProviderResponse(Responses, json)));
  }

  [Fact]
  public void PortableToolResultsStayInTheirOriginalPositionWithoutExecution() {
    var result = new ChatMessage(ChatRole.Tool, new List<AIContent> { new FunctionResultContent("call-1", "already sent result") });
    var user = new ChatMessage(ChatRole.User, "Next");
    ChatMessage[] messages = [result, user];

    IReadOnlyList<ChatMessage> prepared = LlmProviderContext.Prepare(messages, Responses);

    Assert.Same(result, prepared[0]);
    Assert.Same(user, prepared[1]);
    Assert.Equal("already sent result", Assert.IsType<FunctionResultContent>(Assert.Single(prepared[0].Contents)).Result);
  }

  private static ResponseItem Item(string json) => ModelReaderWriter.Read<ResponseItem>(BinaryData.FromString(json))!;

  private static string NativeJson(ResponseItem item) => ModelReaderWriter.Write(item).ToString();

  private static LlmProviderResponse Reload(LlmProviderResponse response) =>
      JsonSerializer.Deserialize<LlmProviderResponse>(JsonSerializer.Serialize(response))!;

  private static void AssertJsonEqual(string expected, string actual) {
    using JsonDocument left = JsonDocument.Parse(expected);
    using JsonDocument right = JsonDocument.Parse(actual);
    Assert.True(JsonElement.DeepEquals(left.RootElement, right.RootElement), $"Expected {expected}; actual {actual}");
  }
}

#pragma warning restore OPENAI001
