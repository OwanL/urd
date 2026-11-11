using System.Net;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Urd.Backend.Tests.Support;
using Urd.Core.Providers;
using Urd.Core.Tools;
using Xunit;
using static Urd.Backend.Tests.Support.OpenAIStreams;
using static Urd.Backend.Tests.Support.ProviderModelStreams;
using static Urd.Backend.Tests.Support.TestAsync;

#pragma warning disable MEAI001

namespace Urd.Backend.Tests.Providers;

public sealed class ProviderModelClientTests {
  [Theory]
  [InlineData(LlmApi.OpenAIResponses)]
  [InlineData(LlmApi.OpenAIChatCompletions)]
  public async Task ForegroundToolImagesUseNativeProviderSerialization(LlmApi api) {
    byte[] firstImage = [0, 1, 2, 253, 254, 255];
    byte[] secondImage = [9, 8, 7, 6, 5];
    const string details = "DETAILS_SENTINEL";
    var firstOutput = new ToolOutput("first result text", [new DataContent(firstImage, "image/png")],
        JsonSerializer.SerializeToElement(new { privateDetails = details }));
    var middleOutput = new ToolOutput("middle result text", [],
        JsonSerializer.SerializeToElement(new { privateDetails = details }));
    var lastOutput = new ToolOutput("last result text", [new DataContent(secondImage, "image/jpeg")],
        JsonSerializer.SerializeToElement(new { privateDetails = details }));
    ChatMessage[] history = [
      new(ChatRole.User, "read these files"),
      new(ChatRole.Assistant, new AIContent[] {
        new FunctionCallContent("call-first", "read", new Dictionary<string, object?>()),
        new FunctionCallContent("call-middle", "read", new Dictionary<string, object?>()),
        new FunctionCallContent("call-last", "read", new Dictionary<string, object?>())
      }),
      new(ChatRole.Tool, new AIContent[] {
        ForegroundResult("call-first", firstOutput),
        ForegroundResult("call-middle", middleOutput),
        ForegroundResult("call-last", lastOutput)
      })
    ];
    using var handler = new ScriptedOpenAIHandler(api == LlmApi.OpenAIResponses
        ? Responses([Message]) : ChatStream());
    using var http = new HttpClient(handler);

    await foreach (ChatResponseUpdate _ in new ProviderModelClient(http).GetStreamingResponseAsync(
        State(api), history, Options(), default)) {
      // Drain the scripted response before inspecting its recorded request.
    }

    ScriptedOpenAIRequest request = Assert.Single(handler.Requests);
    Assert.DoesNotContain("DETAILS_SENTINEL", request.Body);
    using JsonDocument document = JsonDocument.Parse(request.Body);
    JsonElement root = document.RootElement;
    if (api == LlmApi.OpenAIResponses) {
      JsonElement[] input = root.GetProperty("input").EnumerateArray().ToArray();
      JsonElement[] calls = input.Where(item => item.TryGetProperty("type", out JsonElement type) &&
          type.GetString() == "function_call").ToArray();
      Assert.Equal(["call-first", "call-middle", "call-last"], calls.Select(item => item.GetProperty("call_id").GetString()));
      JsonElement[] results = input.Where(item => item.TryGetProperty("type", out JsonElement type) &&
          type.GetString() == "function_call_output").ToArray();
      Assert.Equal(["call-first", "call-middle", "call-last"], results.Select(item => item.GetProperty("call_id").GetString()));
      JsonElement[] firstParts = results[0].GetProperty("output").EnumerateArray().ToArray();
      Assert.Equal("input_text", firstParts[0].GetProperty("type").GetString());
      Assert.Equal("first result text", firstParts[0].GetProperty("text").GetString());
      Assert.Equal("input_image", firstParts[1].GetProperty("type").GetString());
      AssertImageBytes(firstParts[1], "image/png", firstImage);
      Assert.Equal("middle result text", results[1].GetProperty("output").GetString());
      JsonElement[] lastParts = results[2].GetProperty("output").EnumerateArray().ToArray();
      Assert.Equal("last result text", lastParts[0].GetProperty("text").GetString());
      Assert.Equal("input_image", lastParts[1].GetProperty("type").GetString());
      AssertImageBytes(lastParts[1], "image/jpeg", secondImage);
    } else {
      JsonElement[] messages = root.GetProperty("messages").EnumerateArray().ToArray();
      JsonElement assistant = messages.Single(message => message.GetProperty("role").GetString() == "assistant");
      JsonElement[] calls = assistant.GetProperty("tool_calls").EnumerateArray().ToArray();
      Assert.Equal(["call-first", "call-middle", "call-last"], calls.Select(call => call.GetProperty("id").GetString()));
      JsonElement[] results = messages.Where(message => message.GetProperty("role").GetString() == "tool").ToArray();
      Assert.Equal(["call-first", "call-middle", "call-last"], results.Select(result => result.GetProperty("tool_call_id").GetString()));
      Assert.Equal("first result text", results[0].GetProperty("content").GetString());
      Assert.Equal("middle result text", results[1].GetProperty("content").GetString());
      Assert.Equal("last result text", results[2].GetProperty("content").GetString());

      int lastToolIndex = Array.FindLastIndex(messages,
          message => message.GetProperty("role").GetString() == "tool");
      JsonElement[] imageMessages = messages.Skip(lastToolIndex + 1)
          .Where(message => message.GetProperty("role").GetString() == "user" &&
              message.GetProperty("content").ValueKind == JsonValueKind.Array).ToArray();
      Assert.Equal(2, imageMessages.Length);
      Assert.Contains("call_id=call-first", imageMessages[0].GetProperty("content")[0].GetProperty("text").GetString());
      Assert.Contains("origin=tool result", imageMessages[0].GetProperty("content")[0].GetProperty("text").GetString());
      Assert.Contains("untrusted", imageMessages[0].GetProperty("content")[0].GetProperty("text").GetString());
      AssertImageBytes(imageMessages[0].GetProperty("content")[1], "image/png", firstImage);
      Assert.Contains("call_id=call-last", imageMessages[1].GetProperty("content")[0].GetProperty("text").GetString());
      AssertImageBytes(imageMessages[1].GetProperty("content")[1], "image/jpeg", secondImage);
    }
  }

  [Theory]
  [InlineData(LlmApi.OpenAIResponses)]
  [InlineData(LlmApi.OpenAIChatCompletions)]
  public async Task CallsAndFinishRemainWithheldWhenNativeStreamCleanupFails(LlmApi api) {
    using var handler = new DisposalFailingOpenAIHandler(
        api == LlmApi.OpenAIResponses ? Responses([Message, Call]) : ChatCalls());
    using var http = new HttpClient(handler);
    List<ChatResponseUpdate> observed = [];

    var error = await Assert.ThrowsAsync<ProviderOperationException>(async () => {
      await foreach (var update in new ProviderModelClient(http).GetStreamingResponseAsync(
          State(api), History(), Options(tools: true), default))
        observed.Add(update);
    });

    Assert.Equal(ProviderFailure.ProviderUnavailable, error.Failure);
    Assert.Null(error.InnerException);
    Assert.Equal(1, handler.Disposals);
    Assert.DoesNotContain(observed.SelectMany(update => update.Contents), content => content is FunctionCallContent);
    Assert.All(observed, update => Assert.Null(update.FinishReason));
  }

  [Theory]
  [InlineData("response.failed", ProviderFailure.ProviderUnavailable)]
  [InlineData("response.incomplete", ProviderFailure.IncompleteResponse)]
  [InlineData(null, ProviderFailure.IncompleteResponse)]
  public async Task ResponsesFailureOrMissingTerminalNeverReleasesCalls(string? terminal, ProviderFailure failure) =>
      await Rejected(Responses([Message, Call], terminal), failure, options: Options(tools: true));

  [Theory]
  [InlineData("")]
  [InlineData(",\"namespace\":\"other\"")]
  public async Task PlanRejectsUnqualifiedOrWrongNamespaceCalls(string ns) {
    string call = Call.Replace("\"name\":\"lookup\"", "\"name\":\"lookup\"" + ns, StringComparison.Ordinal);
    await Rejected(Responses([Message, call]), ProviderFailure.InvalidProviderResponse,
        State(route: ProviderRoute.ChatGpt), Options(tools: true));
  }

  [Theory]
  [InlineData("\"reasoning\":42")]
  [InlineData("\"reasoning_content\":42")]
  [InlineData("\"reasoning\":\"other\",\"reasoning_content\":\"thought\"")]
  public async Task InvalidOrAmbiguousReasoningCannotAuthorizeCalls(string replacement) =>
      await Rejected(ChatCalls(reasoning: true).Replace(
          "\"reasoning_content\":\"thought\"", replacement, StringComparison.Ordinal),
          ProviderFailure.InvalidProviderResponse, State(LlmApi.OpenAIChatCompletions), Options(tools: true));

  [Theory]
  [InlineData(
      "{\"error\":{\"code\":\"context_length_exceeded\",\"message\":\"private-token\"}}",
      ProviderFailure.ContextWindowExceeded)]
  [InlineData(
      "{\"error\":{\"code\":\"invalid_request_error\",\"message\":\"context_length_exceeded private-token\"}}",
      ProviderFailure.ProviderUnavailable)]
  [InlineData(
      "{\"error\":{\"message\":\"context_length_exceeded private-token\"}}",
      ProviderFailure.ProviderUnavailable)]
  public async Task HttpFailuresClassifyOnlyAnExplicitContextWindowCode(string body, ProviderFailure failure) {
    using var handler = new ScriptedOpenAIHandler(body, "application/json", HttpStatusCode.BadRequest);
    using var http = new HttpClient(handler);

    var error = await Assert.ThrowsAsync<ProviderOperationException>(async () => {
      await foreach (var _ in new ProviderModelClient(http).GetStreamingResponseAsync(
          State(), History(), Options(), default)) { /* Exhaust the stream to observe its terminal failure. */ }
    });

    Assert.Equal(failure, error.Failure);
    Assert.Equal(failure.ToString(), error.Message);
    Assert.Null(error.InnerException);
    Assert.DoesNotContain("private-token", error.ToString());
  }

  [Theory]
  [InlineData("response.failed")]
  [InlineData("error")]
  public async Task ResponsesStreamClassifiesExplicitContextWindowErrors(string terminal) {
    string wire = terminal == "error"
        ? ResponseStream(terminal).Replace("server_error", "context_length_exceeded", StringComparison.Ordinal)
        : Responses(terminal: terminal).Replace("server_error", "context_length_exceeded", StringComparison.Ordinal);
    await Rejected(wire, ProviderFailure.ContextWindowExceeded);
  }

  [Theory]
  [InlineData(HttpStatusCode.Unauthorized, ProviderFailure.AuthenticationRequired)]
  [InlineData(HttpStatusCode.Forbidden, ProviderFailure.PlanAccessDenied)]
  [InlineData(HttpStatusCode.InternalServerError, ProviderFailure.ProviderUnavailable)]
  [InlineData(HttpStatusCode.BadRequest, ProviderFailure.ProviderUnavailable)]
  public async Task ErrorsAreSecretSafeAndNeverRetried(HttpStatusCode status, ProviderFailure failure) {
    using var handler = new ScriptedOpenAIHandler("{\"error\":{\"message\":\"private-token\"}}", "application/json", status);
    using var http = new HttpClient(handler);

    var error = await Assert.ThrowsAsync<ProviderOperationException>(async () => {
      await foreach (var update in new ProviderModelClient(http).GetStreamingResponseAsync(
          State(), History(), Options(), default))
        Assert.Fail($"HTTP failure yielded content: {update}");
    });

    Assert.Equal(failure, error.Failure);
    Assert.Equal(failure.ToString(), error.Message);
    Assert.Null(error.InnerException);
    Assert.DoesNotContain("private-token", error.ToString());
    Assert.Single(handler.Requests);
  }

  private static FunctionResultContent ForegroundResult(string callId, ToolOutput output) =>
      new(callId, output.Images.Count == 0 ? output.Text :
          new AIContent[] { new TextContent(output.Text) }.Concat(output.Images).ToArray());

  private static void AssertImageBytes(JsonElement image, string mediaType, byte[] expected) {
    JsonElement imageUrl = image.GetProperty("image_url");
    string dataUrl = imageUrl.ValueKind == JsonValueKind.String
        ? imageUrl.GetString()! : imageUrl.GetProperty("url").GetString()!;
    string prefix = $"data:{mediaType};base64,";
    Assert.StartsWith(prefix, dataUrl);
    Assert.Equal(expected, Convert.FromBase64String(dataUrl[prefix.Length..]));
  }

  private static async Task Rejected(string wire, ProviderFailure failure, ProviderState? state = null, ChatOptions? options = null) {
    using var handler = new ScriptedOpenAIHandler(wire);
    using var http = new HttpClient(handler);
    List<ChatResponseUpdate> observed = [];

    var error = await Assert.ThrowsAsync<ProviderOperationException>(async () => {
      await foreach (var update in new ProviderModelClient(http).GetStreamingResponseAsync(
          state ?? State(), History(), options ?? Options(), default))
        observed.Add(update);
    });

    Assert.Equal(failure, error.Failure);
    Assert.DoesNotContain(observed.SelectMany(update => update.Contents), content => content is FunctionCallContent);
    Assert.All(observed, update => Assert.Null(update.FinishReason));
    Assert.All(observed, update => Assert.Null(LlmProviderContext.GetResponse(update)));
  }

  private static ChatOptions Options(bool tools = false) => new() {
    ModelId = "fixture-model",
    Tools = tools ? [AIFunctionFactory.Create((string key) => key, name: "lookup")] : null
  };

  private static ChatMessage[] History() => [new(ChatRole.User, "hello")];

  private static ProviderState State(LlmApi api = LlmApi.OpenAIResponses, ProviderRoute route = ProviderRoute.Custom) => new(
      new(Guid.NewGuid(), "Fixture", route, api, new Uri("https://fixture.invalid/prefix/v1"), AuthenticationMethod.None),
      7, 2, null, null, false, null);
}

#pragma warning restore MEAI001
