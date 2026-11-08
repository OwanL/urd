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

  private static ResponseItem Item(string json) => ModelReaderWriter.Read<ResponseItem>(BinaryData.FromString(json))!;

  private static LlmProviderResponse Reload(LlmProviderResponse response) =>
      JsonSerializer.Deserialize<LlmProviderResponse>(JsonSerializer.Serialize(response))!;
}

#pragma warning restore OPENAI001
