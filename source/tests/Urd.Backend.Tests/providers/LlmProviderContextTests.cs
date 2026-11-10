using System.ClientModel.Primitives;
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

    ChatMessage portable = Assert.Single(LlmProviderContext.Prepare([LlmProviderContext.Restore(Reload(captured))], target));

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
  [InlineData("reasoning_content")]
  [InlineData("reasoning")]
  public void ChatNativeReasoningIsRetainedOnlyWithinItsScope(string field) {
    var message = new ChatMessage(ChatRole.Assistant, "answer");
    message.Contents.Add(new TextReasoningContent("private thought"));
    LlmProviderResponse captured = LlmProviderContext.CaptureChat(Chat, message, field);
    ChatMessage restored = LlmProviderContext.Restore(Reload(captured));

    ChatMessage sameScope = Assert.Single(LlmProviderContext.Prepare([restored], Chat));
    using var wire = JsonDocument.Parse(ModelReaderWriter.Write(Assert.IsType<AssistantChatMessage>(sameScope.RawRepresentation)));
    Assert.Equal("private thought", wire.RootElement.GetProperty(field).GetString());

    ChatMessage changed = Assert.Single(LlmProviderContext.Prepare([restored], Chat with { ModelId = "another-model" }));
    Assert.Equal("answer", changed.Text);
    Assert.Empty(changed.Contents.OfType<TextReasoningContent>());
    Assert.Null(changed.RawRepresentation);
  }

  [Fact]
  public void UnknownNativeItemFailsInsteadOfDisappearingOnScopeChange() {
    LlmProviderResponse captured = LlmProviderContext.CaptureResponses(Responses, [
      Item("""{"type":"future_visible_item","payload":{"position":1}}""")
    ]);
    ChatMessage restored = LlmProviderContext.Restore(Reload(captured));

    Assert.Throws<NotSupportedException>(() => LlmProviderContext.Prepare([restored], Chat));
  }

  [Fact]
  public void ToolResultsAcceptOnlyTextAndInMemoryImages() {
    var unsupported = new ChatMessage(ChatRole.Tool, [
      new FunctionResultContent("call-1", new AIContent[] { new TextContent("text"), new AIContent() })
    ]);
    var single = new ChatMessage(ChatRole.Tool, [
      new FunctionResultContent("call-2", new TextContent("not a list"))
    ]);
    var uriImage = new ChatMessage(ChatRole.Tool, [
      new FunctionResultContent("call-3", new AIContent[] {
        new TextContent("text"), new UriContent(new Uri("https://example.invalid/image.png"), "image/png")
      })
    ]);
    byte[] assistantImageBytes = [1, 2, 3];
    var assistantImage = new ChatMessage(ChatRole.Assistant, [new DataContent(assistantImageBytes, "image/png")]);

    Assert.Throws<NotSupportedException>(() => LlmProviderContext.Prepare([unsupported], Chat));
    Assert.Throws<NotSupportedException>(() => LlmProviderContext.Prepare([single], Chat));
    Assert.Throws<NotSupportedException>(() => LlmProviderContext.Prepare([uriImage], Chat));
    Assert.Throws<NotSupportedException>(() => LlmProviderContext.Prepare([assistantImage], Chat));
    Assert.Throws<NotSupportedException>(() => LlmProviderContext.ForModel([unsupported], supportsImages: false));
  }

  [Fact]
  public void ForModelPreservesNestedImagesWhenSupportedAndLabelsOmittedImagesOtherwise() {
    byte[] bytes = [1, 2, 3, 4];
    var call = new FunctionCallContent("call-image", "read", new Dictionary<string, object?>());
    AIContent[] nested = [new TextContent("image text"), new DataContent(bytes, "image/png")];
    var result = new FunctionResultContent("call-image", nested);
    var assistant = new ChatMessage(ChatRole.Assistant, [call]);
    var tool = new ChatMessage(ChatRole.Tool, [result]);
    ChatMessage[] history = [assistant, tool];

    IReadOnlyList<ChatMessage> imageModel = LlmProviderContext.ForModel(history, supportsImages: true);
    Assert.Same(assistant, imageModel[0]);
    Assert.Same(tool, imageModel[1]);
    Assert.Same(result, Assert.Single(imageModel[1].Contents.OfType<FunctionResultContent>()));
    Assert.Same(nested, Assert.IsType<AIContent[]>(result.Result));

    IReadOnlyList<ChatMessage> textModel = LlmProviderContext.ForModel(history, supportsImages: false);
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
