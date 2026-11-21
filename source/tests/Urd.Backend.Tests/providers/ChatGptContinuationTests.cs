using System.Text.Json;
using Urd.Core.Providers;
using Xunit;

namespace Urd.Backend.Tests.Providers;

public sealed class ChatGptContinuationTests {
  private const string User = "{\"role\":\"user\",\"content\":\"hello\"}";
  private const string Output = "{\"id\":\"msg_1\",\"type\":\"message\",\"role\":\"assistant\",\"content\":[{\"type\":\"output_text\",\"text\":\"hi\"}]}";
  private const string Next = "{\"role\":\"user\",\"content\":\"next\"}";

  [Fact]
  public void AppendsOnlyNewInputAfterNativeOutput() {
    var continuation = new ChatGptContinuation();
    Frame(continuation, Request(User));
    continuation.Complete(Response(Output));
    JsonElement frame = Frame(continuation, Request($"{User},{Output},{Next}"));
    Assert.Equal("response.create", frame.GetProperty("type").GetString());
    Assert.Equal("resp_1", frame.GetProperty("previous_response_id").GetString());
    Assert.Equal(Next, frame.GetProperty("input")[0].GetRawText());
    Assert.Equal(1, frame.GetProperty("input").GetArrayLength());
  }

  [Theory]
  [InlineData("model", "other")]
  [InlineData("tools", "[]")]
  [InlineData("temperature", "1")]
  public void ConfigurationDivergenceFallsBack(string field, string value) {
    var continuation = Ready();
    string json = field == "model" ? $"{{\"model\":\"{value}\",\"input\":[{User},{Output},{Next}]}}" :
        $"{{\"model\":\"model\",\"{field}\":{value},\"input\":[{User},{Output},{Next}]}}";
    AssertFull(Frame(continuation, json), 3);
  }

  [Fact]
  public void ReorderedAndDifferentlyEscapedOutputMatches() {
    var continuation = Ready();
    string replay = "{\"content\":[{\"text\":\"h\\u0069\",\"type\":\"output_text\"}],\"role\":\"assistant\",\"type\":\"message\",\"id\":\"msg_1\"}";
    JsonElement frame = Frame(continuation, Request($"{User},{replay},{Next}"));
    Assert.Equal("resp_1", frame.GetProperty("previous_response_id").GetString());
    Assert.Equal(1, frame.GetProperty("input").GetArrayLength());
  }

  [Fact]
  public void ChangedNestedContentAndArrayOrderFallBack() {
    var continuation = new ChatGptContinuation();
    Frame(continuation, Request(User));
    continuation.Complete(Response("{\"data\":[{\"a\":1,\"b\":\"hi\"},2]}"));
    AssertFull(Frame(continuation, Request($"{User},{{\"data\":[{{\"b\":\"changed\",\"a\":1}},2]}},{Next}")), 3);
    continuation = new ChatGptContinuation();
    Frame(continuation, Request(User));
    continuation.Complete(Response("{\"data\":[1,2]}"));
    AssertFull(Frame(continuation, Request($"{User},{{\"data\":[2,1]}},{Next}")), 3);
  }

  [Theory]
  [InlineData("type", "\"response.create\"")]
  [InlineData("previous_response_id", "\"resp_other\"")]
  public void RejectsReservedRequestProperties(string name, string value) {
    var continuation = Ready();
    Assert.Throws<ArgumentException>(() => Frame(continuation, $"{{\"{name}\":{value},\"input\":[{User}]}}"));
    AssertFull(Frame(continuation, Request($"{User},{Output},{Next}")), 3);
  }

  [Theory]
  [InlineData("{}")]
  [InlineData("{\"id\":\"resp_1\",\"output\":[{}]}")]
  [InlineData("{\"id\":\"resp_1\",\"status\":\"failed\",\"output\":[{}]}")]
  [InlineData("{\"id\":\"resp_1\",\"status\":\"completed\",\"output\":[]}")]
  [InlineData("{\"type\":\"response.completed\",\"response\":{\"id\":\"resp_1\",\"output\":[{}]}}")]
  public void InvalidCompletionResets(string response) {
    var continuation = new ChatGptContinuation();
    Frame(continuation, Request(User));
    continuation.Complete(Parse(response));
    AssertFull(Frame(continuation, Request($"{User},{Output},{Next}")), 3);
  }

  [Fact]
  public void ChangedPriorContentAndOutputFallBack() {
    AssertFull(Frame(Ready(), Request($"{Next},{Output},{Next}")), 3);
    AssertFull(Frame(Ready(), Request($"{User},{{\"type\":\"message\"}},{Next}")), 3);
  }

  [Fact]
  public void SameInputAndResetFallBack() {
    AssertFull(Frame(Ready(), Request($"{User},{Output}")), 2);
    var continuation = Ready();
    continuation.Reset();
    AssertFull(Frame(continuation, Request($"{User},{Output},{Next}")), 3);
  }

  [Fact]
  public void IncompleteExchangeAndEmptyOutputDoNotLeaveCheckpoint() {
    var continuation = Ready();
    Frame(continuation, Request($"{User},{Output},{Next}"));
    AssertFull(Frame(continuation, Request($"{User},{Output},{Next}")), 3);
    continuation.Complete(Response(""));
    AssertFull(Frame(continuation, Request($"{User},{Output},{Next}")), 3);
    continuation.Complete(Parse("{\"type\":\"response.incomplete\"}"));
    AssertFull(Frame(continuation, Request($"{User},{Output},{Next}")), 3);
  }

  [Fact]
  public void ChangedSuffixIsAllowedAndCommittedForNextExchange() {
    var continuation = Ready();
    JsonElement first = Frame(continuation, Request($"{User},{Output},{Next}"));
    Assert.Equal(Next, first.GetProperty("input")[0].GetRawText());
    continuation.Complete(Response(Output));
    AssertFull(Frame(continuation, Request($"{User},{Output},{User},{Output},{Next}")), 5);
  }

  private static ChatGptContinuation Ready() {
    var continuation = new ChatGptContinuation();
    Frame(continuation, Request(User));
    continuation.Complete(Response(Output));
    return continuation;
  }

  private static string Request(string input) => $"{{\"model\":\"model\",\"input\":[{input}]}}";
  private static JsonElement Response(string output) => Parse($"{{\"id\":\"resp_1\",\"status\":\"completed\",\"output\":[{output}]}}");
  private static JsonElement Frame(ChatGptContinuation continuation, string request) => Parse(continuation.CreateFrame(Parse(request)));
  private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();
  private static JsonElement Parse(byte[] json) => JsonDocument.Parse(json).RootElement.Clone();
  private static void AssertFull(JsonElement frame, int count) {
    Assert.False(frame.TryGetProperty("previous_response_id", out _));
    Assert.Equal(count, frame.GetProperty("input").GetArrayLength());
  }
}
