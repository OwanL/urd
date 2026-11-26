using System.ClientModel.Primitives;
using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenAI.Chat;
using OpenAI.Responses;
using Urd.Backend.Tests.Support;
using Urd.Core.Providers;
using Xunit;
using static Urd.Backend.Tests.Support.OpenAIStreams;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

#pragma warning disable OPENAI001 // Exercise the pinned SDK's real native persistence APIs offline.

namespace Urd.Backend.Tests.Providers;

public sealed class LlmProviderContextTests {
  private static readonly LlmProviderScope Responses = new(Guid.NewGuid(), 3, LlmApi.OpenAIResponses, "requested-model");
  private static readonly LlmProviderScope Chat = Responses with { Api = LlmApi.OpenAIChatCompletions };

  [Theory]
  [InlineData("connection")]
  [InlineData("generation")]
  [InlineData("api")]
  [InlineData("model")]
  public void ChangedScopeKeepsVisibleTextAndCallsButOmitsPrivateReasoning(string change) {
    LlmProviderResponse captured = LlmProviderContext.CaptureResponses(Responses, [
      Item("""{"type":"message","role":"assistant","content":[{"type":"output_text","text":"Visible","annotations":[]}] }"""),
      Item("""{"type":"reasoning","summary":[{"type":"summary_text","text":"Private"}],"encrypted_content":"opaque"}"""),
      Item("""{"type":"function_call","call_id":"call-1","name":"lookup","arguments":"{\"value\":1}"}""")
    ]);
    LlmProviderScope target = change switch {
      "connection" => Responses with { ConnectionId = Guid.NewGuid() },
      "generation" => Responses with { Generation = Responses.Generation + 1 },
      "api" => Chat,
      _ => Responses with { ModelId = "another-requested-model" }
    };

    ChatMessage portable = Assert.Single(LlmMessageProjection.Prepare([LlmProviderContext.Restore(Reload(captured))], target));

    Assert.Equal("Visible", portable.Text);
    Assert.Null(portable.RawRepresentation);
    Assert.DoesNotContain(portable.Contents, content => content is TextReasoningContent);
    Assert.All(portable.Contents, content => Assert.Null(content.RawRepresentation));
    FunctionCallContent call = Assert.Single(portable.Contents.OfType<FunctionCallContent>());
    Assert.Equal("call-1", call.CallId);
    Assert.Equal("lookup", call.Name);
    Assert.Equal("{\"value\":1}", JsonSerializer.Serialize(call.Arguments));
  }

  [Theory]
  [InlineData(ProviderRoute.OpenAI, LlmApi.OpenAIResponses)]
  [InlineData(ProviderRoute.ChatGpt, LlmApi.OpenAIResponses)]
  [InlineData(ProviderRoute.Custom, LlmApi.OpenAIResponses)]
  [InlineData(ProviderRoute.Custom, LlmApi.OpenAIChatCompletions)]
  public async Task ModelSwitchSendsVisibleHistoryInTheDestinationFormat(ProviderRoute route, LlmApi api) {
    LlmProviderResponse captured = LlmProviderContext.CaptureResponses(Responses, [
      Item("""{"type":"message","id":"old-message-1","role":"assistant","phase":"commentary","extra_metadata":"ignored","content":[{"type":"output_text","text":"Work","annotations":[],"extra_part_metadata":true},{"type":"output_text","text":"ing","annotations":[]}]}"""),
      Item("""{"type":"reasoning","id":"old-reasoning","summary":[{"type":"summary_text","text":"private thought"}],"encrypted_content":"private opaque"}"""),
      Item("""{"type":"function_call","id":"old-function-item","call_id":"call-1","name":"lookup","namespace":"urd","arguments":"{\"value\":1}","extra_call_metadata":true}"""),
      Item("""{"type":"message","id":"old-message-2","role":"assistant","phase":"final_answer","content":[{"type":"output_text","text":"Done","annotations":[]}]}"""),
      Item("""{"type":"message","id":"old-message-3","role":"assistant","phase":"future_phase","content":[{"type":"output_text","text":"Future","annotations":[]}]}""")
    ]);
    var state = new ProviderState(new(Responses.ConnectionId, "Fixture", route, api,
        new Uri("https://fixture.invalid/v1/"), AuthenticationMethod.None), Responses.Generation, 2, null, null, false, null);
    using var handler = new ScriptedOpenAIHandler(api == LlmApi.OpenAIResponses ? ProviderModelStreams.Responses() : ChatStream());
    using var http = new HttpClient(handler);
    await foreach (var _ in new OpenAIInferenceClient(http).GetStreamingResponseAsync(state,
        [LlmProviderContext.Restore(Reload(captured))], new ChatOptions { ModelId = "new-model" }, default)) { /* Drain the stream. */ }

    string body = Assert.Single(handler.Requests).Body;
    Assert.DoesNotContain("private thought", body);
    Assert.DoesNotContain("private opaque", body);
    Assert.DoesNotContain("old-message", body);
    Assert.DoesNotContain("old-function-item", body);
    Assert.DoesNotContain("old-reasoning", body);
    Assert.DoesNotContain("extra_metadata", body);
    using var document = JsonDocument.Parse(body);
    JsonElement input = document.RootElement.GetProperty(api == LlmApi.OpenAIResponses ? "input" : "messages");
    string wire = input.GetRawText();
    Assert.Contains("Working", wire);
    Assert.Contains("Done", wire);
    Assert.Contains("Future", wire);
    Assert.DoesNotContain("future_phase", wire);
    Assert.Contains("lookup", wire);
    Assert.Contains("call-1", wire);
    Assert.Contains("value", wire);
    if (api == LlmApi.OpenAIResponses) {
      JsonElement[] items = input.EnumerateArray().ToArray();
      Assert.Equal(4, items.Length);
      Assert.Equal("commentary", items[0].GetProperty("phase").GetString());
      Assert.Equal("function_call", items[1].GetProperty("type").GetString());
      Assert.Equal("final_answer", items[2].GetProperty("phase").GetString());
      Assert.False(items[3].TryGetProperty("phase", out _));
      if (route == ProviderRoute.ChatGpt)
        Assert.Equal("urd", items[1].GetProperty("namespace").GetString());
      else
        Assert.False(items[1].TryGetProperty("namespace", out _));
    } else {
      Assert.DoesNotContain("commentary", wire);
      Assert.DoesNotContain("final_answer", wire);
    }
  }

  [Theory]
  [InlineData("reasoning_content")]
  [InlineData("reasoning")]
  public void ChatNativeReasoningIsRetainedOnlyWithinItsScope(string field) {
    var message = new ChatMessage(ChatRole.Assistant, "answer");
    message.Contents.Add(new TextReasoningContent("private thought"));
    LlmProviderResponse captured = LlmProviderContext.CaptureChat(Chat, message, field);
    ChatMessage restored = LlmProviderContext.Restore(Reload(captured));

    ChatMessage sameScope = Assert.Single(LlmMessageProjection.Prepare([restored], Chat));
    using var wire = JsonDocument.Parse(ModelReaderWriter.Write(Assert.IsType<AssistantChatMessage>(sameScope.RawRepresentation)));
    Assert.Equal("private thought", wire.RootElement.GetProperty(field).GetString());

    ChatMessage changed = Assert.Single(LlmMessageProjection.Prepare([restored], Chat with { ModelId = "another-model" }));
    Assert.Equal("answer", changed.Text);
    Assert.Empty(changed.Contents.OfType<TextReasoningContent>());
    Assert.Null(changed.RawRepresentation);
  }

  [Fact]
  public void AdaptForModelPreservesNestedImagesWhenSupportedAndLabelsOmittedImagesOtherwise() {
    byte[] bytes = [1, 2, 3, 4];
    var call = new FunctionCallContent("call-image", "read", new Dictionary<string, object?>());
    AIContent[] nested = [new TextContent("image text"), new DataContent(bytes, "image/png")];
    var result = new FunctionResultContent("call-image", nested);
    var assistant = new ChatMessage(ChatRole.Assistant, [call]);
    var tool = new ChatMessage(ChatRole.Tool, [result]);
    ChatMessage[] history = [assistant, tool];

    IReadOnlyList<ChatMessage> imageModel = LlmMessageProjection.AdaptForModel(history, supportsImages: true);
    Assert.Same(assistant, imageModel[0]);
    Assert.Same(tool, imageModel[1]);
    Assert.Same(result, Assert.Single(imageModel[1].Contents.OfType<FunctionResultContent>()));
    Assert.Same(nested, Assert.IsType<AIContent[]>(result.Result));

    IReadOnlyList<ChatMessage> textModel = LlmMessageProjection.AdaptForModel(history, supportsImages: false);
    Assert.NotSame(tool, textModel[1]);
    FunctionResultContent filtered = Assert.Single(textModel[1].Contents.OfType<FunctionResultContent>());
    Assert.Equal("call-image", filtered.CallId);
    IReadOnlyList<AIContent> filteredContents = Assert.IsAssignableFrom<IReadOnlyList<AIContent>>(filtered.Result);
    Assert.Equal(["image text", "\n[Images omitted because the current model does not support images.]"],
        filteredContents.OfType<TextContent>().Select(content => content.Text));
    Assert.DoesNotContain(filteredContents, content => content is DataContent);
    Assert.Same(nested, Assert.IsType<AIContent[]>(result.Result));
    Assert.Equal("call-image", Assert.Single(textModel[0].Contents.OfType<FunctionCallContent>()).CallId);
  }

  private static ResponseItem Item(string json) => ModelReaderWriter.Read<ResponseItem>(BinaryData.FromString(json))!;

  private static LlmProviderResponse Reload(LlmProviderResponse response) =>
      JsonSerializer.Deserialize<LlmProviderResponse>(JsonSerializer.Serialize(response))!;
}

#pragma warning restore OPENAI001
