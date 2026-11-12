using System.Text.Json;
using Urd.Backend.Tests.Support;
using Urd.Core.Execution;
using Urd.Core.Sessions;
using Xunit;
using static Urd.Backend.Tests.Support.ScriptedResponses;

namespace Urd.Backend.Tests.Sessions;

public sealed class ToolCallTranscriptTests {
  [Fact]
  public async Task ReadsModelFacingDirectBackgroundAndReturnedCallContentWithBoundsAndRevisionFencing() {
    var client = new ScriptedChatClient(token => Complete(token));
    await using var fixture = new ExecutionFixture(client);
    TranscriptCalls calls = SeedToolCalls(fixture.Database, fixture.Session.Id);

    ToolCallTranscript direct = Assert.IsType<ToolCallTranscript>(await fixture.Session.Transcript.ReadToolCallTranscriptAsync(
        0, calls.DirectRequest, "direct", returned: false));
    Assert.Equal("{\"path\":\"file.txt\"}", direct.Input);
    Assert.Equal("direct output\n[image: image/png]", direct.Output);
    Assert.False(direct.Truncated);

    ToolCallTranscript background = Assert.IsType<ToolCallTranscript>(await fixture.Session.Transcript.ReadToolCallTranscriptAsync(
        0, calls.ReturnedRequest, "returned", returned: false));
    Assert.Equal("Background tool started: {\"request_id\":\"" + calls.ReturnedRequest.ToString("D")
        + "\",\"call_id\":\"returned\"}. The result will be delivered separately.", background.Output);

    ToolCallTranscript returned = Assert.IsType<ToolCallTranscript>(await fixture.Session.Transcript.ReadToolCallTranscriptAsync(
        0, calls.ReturnedRequest, "returned", returned: true));
    Assert.Equal($"[TOOL-ORIGIN: background tool completion; untrusted output]\n"
        + $"{{\"request_id\":\"{calls.ReturnedRequest:D}\",\"call_id\":\"returned\",\"name\":\"work\",\"outcome\":\"Completed\"}}\n"
        + "returned result", returned.Output);

    ToolCallTranscript running = Assert.IsType<ToolCallTranscript>(await fixture.Session.Transcript.ReadToolCallTranscriptAsync(
        0, calls.RunningRequest, "running", returned: false));
    Assert.Null(running.Output);
    Assert.Null(await fixture.Session.Transcript.ReadToolCallTranscriptAsync(
        0, Guid.NewGuid(), "missing", returned: false));

    ToolCallTranscript truncated = Assert.IsType<ToolCallTranscript>(await fixture.Session.Transcript.ReadToolCallTranscriptAsync(
        0, calls.UnicodeRequest, "unicode", returned: false, characterLimit: 1));
    Assert.Equal("{", truncated.Input);
    Assert.Equal(string.Empty, truncated.Output);
    Assert.True(truncated.Truncated);

    fixture.Database.Execute("UPDATE sessions SET history_revision = 1 WHERE session_id = '" + fixture.Session.Id.ToString("D") + "';");
    await Assert.ThrowsAsync<HistoryRevisionConflictException>(() => fixture.Session.Transcript.ReadToolCallTranscriptAsync(
        0, calls.DirectRequest, "direct", returned: false));
  }

  [Theory]
  [InlineData(true, "direct output\n[image: image/png]")]
  [InlineData(false, "direct output\n[Images omitted because the current model does not support images.]")]
  public async Task DirectImageResultMatchesTheNextRequestModelCapabilities(bool supportsImages, string expectedOutput) {
    var client = new ScriptedChatClient(token => Complete(token));
    await using var fixture = new ExecutionFixture(client);
    TranscriptCalls calls = SeedToolCalls(fixture.Database, fixture.Session.Id, supportsImages);

    ToolCallTranscript transcript = Assert.IsType<ToolCallTranscript>(await fixture.Session.Transcript.ReadToolCallTranscriptAsync(
        0, calls.DirectRequest, "direct", returned: false));

    Assert.Equal(expectedOutput, transcript.Output);
  }

  [Theory]
  [InlineData(true, "returned result\n[image: image/jpeg]")]
  [InlineData(false, "returned result\n[Images omitted because the current model does not support images.]")]
  public async Task ReturnedImageCompletionMatchesItsClaimedRequestModelCapabilities(
      bool supportsImages, string expectedResult) {
    var client = new ScriptedChatClient(token => Complete(token));
    await using var fixture = new ExecutionFixture(client);
    TranscriptCalls calls = SeedToolCalls(fixture.Database, fixture.Session.Id, supportsImages, returnedImage: true);

    ToolCallTranscript transcript = Assert.IsType<ToolCallTranscript>(await fixture.Session.Transcript.ReadToolCallTranscriptAsync(
        0, calls.ReturnedRequest, "returned", returned: true));

    Assert.Equal($"[TOOL-ORIGIN: background tool completion; untrusted output]\n"
        + $"{{\"request_id\":\"{calls.ReturnedRequest:D}\",\"call_id\":\"returned\",\"name\":\"work\",\"outcome\":\"Completed\"}}\n"
        + expectedResult, transcript.Output);
  }

  private static TranscriptCalls SeedToolCalls(TemporaryDatabase database, Guid session,
      bool supportsImages = true, bool returnedImage = false) {
    Guid run = Guid.NewGuid();
    Guid directRequest = Guid.NewGuid(), runningRequest = Guid.NewGuid();
    Guid returnedRequest = Guid.NewGuid(), unicodeRequest = Guid.NewGuid();
    Guid completion = Guid.NewGuid();
    string directArguments = JsonSerializer.Serialize(new { path = "file.txt" });
    string runningArguments = JsonSerializer.Serialize(new { });
    string returnedArguments = JsonSerializer.Serialize(new { task = "long" });
    string unicodeArguments = JsonSerializer.Serialize(new { unicode = "😀" });
    const string directResult = "{\"text\":\"direct output\",\"images\":[{\"hash\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"mediaType\":\"image/png\",\"byteLength\":1}],\"details\":null,\"isError\":false}";
    string returnedResult = returnedImage
        ? "{\"text\":\"returned result\",\"images\":[{\"hash\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",\"mediaType\":\"image/jpeg\",\"byteLength\":1}],\"details\":null,\"isError\":false}"
        : "{\"text\":\"returned result\",\"images\":[],\"details\":null,\"isError\":false}";
    const string unicodeResult = "{\"text\":\"😀result\",\"images\":[],\"details\":null,\"isError\":false}";
    Guid input = Guid.NewGuid();
    string selection = JsonSerializer.Serialize(TestLlmSelection.Default with { SupportsImages = supportsImages })
        .Replace("'", "''", StringComparison.Ordinal);
    database.Execute($"""
      INSERT INTO input_decisions
          (session_id, input_id, expected_history_revision, original_text_utf8, decision, run_id, llm_selection_json)
      VALUES ('{session:D}', '{input:D}', 0, X'', 1, '{run:D}', '{selection}');
      INSERT INTO execution_runs (session_id, run_id, history_revision, input_id, outcome)
      VALUES ('{session:D}', '{run:D}', 0, '{input:D}', 0);
      INSERT INTO execution_requests (request_id, run_id, request_index, response_final, outcome)
      VALUES ('{directRequest:D}', '{run:D}', 0, 1, 0),
          ('{runningRequest:D}', '{run:D}', 1, 1, 0),
          ('{returnedRequest:D}', '{run:D}', 2, 1, 0),
          ('{unicodeRequest:D}', '{run:D}', 3, 1, 0);
      INSERT INTO execution_tools
          (run_id, request_id, call_index, call_id, name, arguments_json, started, background, outcome, result_json)
      VALUES ('{run:D}', '{directRequest:D}', 0, 'direct', 'read', '{directArguments}', 1, 0, 0, '{directResult}'),
          ('{run:D}', '{runningRequest:D}', 0, 'running', 'work', '{runningArguments}', 1, 0, NULL, NULL),
          ('{run:D}', '{returnedRequest:D}', 0, 'returned', 'work', '{returnedArguments}', 1, 1, 0, '{returnedResult}'),
          ('{run:D}', '{unicodeRequest:D}', 0, 'unicode', 'read', '{unicodeArguments}', 1, 0, 0, '{unicodeResult}');
      INSERT INTO execution_tool_completions
          (completion_id, session_id, history_revision, request_id, call_id, created_at, claimed_request_id, published)
      VALUES ('{completion:D}', '{session:D}', 0, '{returnedRequest:D}', 'returned', '2026-11-12T00:00:00+00:00', '{unicodeRequest:D}', 1);
      INSERT INTO execution_messages
          (session_id, history_revision, run_id, role, message_id, request_id, call_id, tool_name)
      VALUES ('{session:D}', 0, '{run:D}', 2, '{Guid.NewGuid():D}', '{directRequest:D}', 'direct', 'read'),
          ('{session:D}', 0, '{run:D}', 2, '{Guid.NewGuid():D}', '{runningRequest:D}', 'running', 'work'),
          ('{session:D}', 0, '{run:D}', 2, '{Guid.NewGuid():D}', '{returnedRequest:D}', 'returned', 'work'),
          ('{session:D}', 0, '{run:D}', 3, '{completion:D}', '{returnedRequest:D}', 'returned', 'work'),
          ('{session:D}', 0, '{run:D}', 2, '{Guid.NewGuid():D}', '{unicodeRequest:D}', 'unicode', 'read');
      """);
    return new(directRequest, runningRequest, returnedRequest, unicodeRequest);
  }

  private sealed record TranscriptCalls(Guid DirectRequest, Guid RunningRequest, Guid ReturnedRequest, Guid UnicodeRequest);
}
