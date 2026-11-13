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

  [Fact]
  public async Task PreviewAllocationsStayBoundedForLargeArgumentsResultsAndInspectionOnlyDetails() {
    var client = new ScriptedChatClient(token => Complete(token));
    await using var fixture = new ExecutionFixture(client);
    TranscriptCalls calls = SeedToolCalls(fixture.Database, fixture.Session.Id);
    string large = new('x', 2 * 1024 * 1024);
    string arguments = JsonSerializer.Serialize(new { content = large });
    foreach (string text in new[] { large, "short\n😀\t\\\"" }) {
      string result = JsonSerializer.Serialize(new { text, images = Array.Empty<object>(), details = large, isError = false });
      fixture.Database.Execute($"UPDATE execution_tools SET arguments_json = '{arguments}', result_json = '{result}' "
          + $"WHERE request_id = '{calls.DirectRequest:D}';");
      await fixture.Session.Transcript.ReadToolCallTranscriptAsync(0, calls.DirectRequest, "direct", false, 64);
      long before = GC.GetAllocatedBytesForCurrentThread();
      Task<ToolCallTranscript?> read = fixture.Session.Transcript.ReadToolCallTranscriptAsync(
          0, calls.DirectRequest, "direct", false, 64);
      long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
      Assert.True(read.IsCompletedSuccessfully, "The uncontended read must finish on the measured thread.");
      ToolCallTranscript preview = Assert.IsType<ToolCallTranscript>(await read);
      Assert.Equal(arguments[..64], preview.Input);
      Assert.Equal(text[..Math.Min(text.Length, 64)], preview.Output);
      Assert.True(preview.Truncated);
      Assert.True(allocated < 512 * 1024, $"A 64-character preview allocated {allocated:N0} bytes.");
    }
  }

  [Fact]
  public async Task PreviewMatchesFullModelProjectionAtCharacterAndUnicodeBoundaries() {
    foreach (bool supportsImages in new[] { true, false }) {
      var client = new ScriptedChatClient(token => Complete(token));
      await using var fixture = new ExecutionFixture(client);
      TranscriptCalls calls = SeedToolCalls(fixture.Database, fixture.Session.Id, supportsImages, returnedImage: true);
      fixture.Database.Execute($"UPDATE execution_tools SET result_json = json_set(result_json, '$.text', '') "
          + $"WHERE request_id = '{calls.ReturnedRequest:D}';");
      foreach ((Guid request, string call, bool returned) in new[] {
          (calls.DirectRequest, "direct", false), (calls.ReturnedRequest, "returned", false),
          (calls.ReturnedRequest, "returned", true), (calls.RunningRequest, "running", false),
          (calls.UnicodeRequest, "unicode", false) }) {
        ToolCallTranscript full = Assert.IsType<ToolCallTranscript>(await fixture.Session.Transcript.ReadToolCallTranscriptAsync(
            0, request, call, returned));
        int length = Math.Max(full.Input.Length, full.Output?.Length ?? 0);
        foreach (int limit in new[] { 0, 1, 2, 3, 12, 64, 128, length, length + 1 }) {
          ToolCallTranscript preview = Assert.IsType<ToolCallTranscript>(await fixture.Session.Transcript.ReadToolCallTranscriptAsync(
              0, request, call, returned, limit));
          Assert.Equal(ScalarPrefix(full.Input, limit), preview.Input);
          Assert.Equal(ScalarPrefix(full.Output, limit), preview.Output);
          Assert.Equal(full.Input.Length > limit || full.Output?.Length > limit, preview.Truncated);
        }
      }
    }
  }

  private static string? ScalarPrefix(string? value, int limit) {
    int length = 0;
    return value is null ? null : string.Concat(value.EnumerateRunes().TakeWhile(rune => {
      length += rune.Utf16SequenceLength;
      return length <= limit;
    }));
  }

  [Fact]
  public async Task FinishedPayloadIsNotFinalUntilItsModelFacingImageProjectionIsKnown() {
    var client = new ScriptedChatClient(token => Complete(token));
    await using var fixture = new ExecutionFixture(client);
    Guid request = SeedStoppedDirectResult(fixture.Database, fixture.Session.Id, neverAttemptedContinuation: false);
    fixture.Database.Execute($"UPDATE execution_requests SET attempt = 1 WHERE request_id <> '{request:D}';");
    ToolCallTranscript pending = Assert.IsType<ToolCallTranscript>(await fixture.Session.Transcript.ReadToolCallTranscriptAsync(
        0, request, "direct", false, 65536));
    Assert.False(pending.ProjectionFinal);
    Assert.False(pending.Truncated);
    Assert.Equal("direct output\n[image: image/png]", pending.Output);

    fixture.Database.Execute($"UPDATE execution_requests SET attempt = 2 WHERE request_id <> '{request:D}';");
    ToolCallTranscript delivered = Assert.IsType<ToolCallTranscript>(await fixture.Session.Transcript.ReadToolCallTranscriptAsync(
        0, request, "direct", false, 65536));
    Assert.True(delivered.ProjectionFinal);
    Assert.Equal("direct output\n[Images omitted because the current model does not support images.]", delivered.Output);
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

  [Fact]
  public async Task DirectImageResultUsesNextRunThatSendsAfterTheOriginalRunStops() {
    var client = new ScriptedChatClient(token => Complete(token));
    await using var fixture = new ExecutionFixture(client);
    Guid request = SeedStoppedDirectResult(fixture.Database, fixture.Session.Id, neverAttemptedContinuation: false);

    ToolCallTranscript transcript = Assert.IsType<ToolCallTranscript>(await fixture.Session.Transcript.ReadToolCallTranscriptAsync(
        0, request, "direct", returned: false));

    Assert.Equal("direct output\n[Images omitted because the current model does not support images.]", transcript.Output);
  }

  [Fact]
  public async Task NeverAttemptedSameRunContinuationDoesNotMaskTheLaterReceiver() {
    var client = new ScriptedChatClient(token => Complete(token));
    await using var fixture = new ExecutionFixture(client);
    Guid request = SeedStoppedDirectResult(fixture.Database, fixture.Session.Id, neverAttemptedContinuation: true);

    ToolCallTranscript transcript = Assert.IsType<ToolCallTranscript>(await fixture.Session.Transcript.ReadToolCallTranscriptAsync(
        0, request, "direct", returned: false));

    Assert.Equal("direct output\n[Images omitted because the current model does not support images.]", transcript.Output);
  }

  [Fact]
  public async Task DirectImageResultBeforeReceiverCompactionBoundaryKeepsImagePlaceholders() {
    var client = new ScriptedChatClient(token => Complete(token));
    await using var fixture = new ExecutionFixture(client);
    Guid request = SeedStoppedDirectResult(fixture.Database, fixture.Session.Id, neverAttemptedContinuation: false,
        receiverCompaction: ReceiverCompaction.AtReceiver);

    ToolCallTranscript transcript = Assert.IsType<ToolCallTranscript>(await fixture.Session.Transcript.ReadToolCallTranscriptAsync(
        0, request, "direct", returned: false));

    Assert.Equal("direct output\n[image: image/png]", transcript.Output);
  }

  [Fact]
  public async Task DirectImageResultUsesEarlierReceiverAfterLaterCompaction() {
    var client = new ScriptedChatClient(token => Complete(token));
    await using var fixture = new ExecutionFixture(client);
    Guid request = SeedStoppedDirectResult(fixture.Database, fixture.Session.Id, neverAttemptedContinuation: false,
        receiverCompaction: ReceiverCompaction.AfterReceiver);

    ToolCallTranscript transcript = Assert.IsType<ToolCallTranscript>(await fixture.Session.Transcript.ReadToolCallTranscriptAsync(
        0, request, "direct", returned: false));

    Assert.Equal("direct output\n[Images omitted because the current model does not support images.]", transcript.Output);
  }

  [Fact]
  public async Task DirectImageResultOnSameRequestAsCompactionBoundaryStillUsesReceiver() {
    var client = new ScriptedChatClient(token => Complete(token));
    await using var fixture = new ExecutionFixture(client);
    Guid request = SeedStoppedDirectResult(fixture.Database, fixture.Session.Id, neverAttemptedContinuation: false,
        receiverCompaction: ReceiverCompaction.AtSource);

    ToolCallTranscript transcript = Assert.IsType<ToolCallTranscript>(await fixture.Session.Transcript.ReadToolCallTranscriptAsync(
        0, request, "direct", returned: false));

    Assert.Equal("direct output\n[Images omitted because the current model does not support images.]", transcript.Output);
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

  [Theory]
  [InlineData(false, false)]
  [InlineData(true, true)]
  public async Task ReturnedImageCompletionIgnoresAClaimThatDidNotReceiveIt(bool sent, bool compacted) {
    var client = new ScriptedChatClient(token => Complete(token));
    await using var fixture = new ExecutionFixture(client);
    TranscriptCalls calls = SeedToolCalls(fixture.Database, fixture.Session.Id,
        supportsImages: false, returnedImage: true, returnedClaimSent: sent, returnedClaimCompacted: compacted);

    ToolCallTranscript transcript = Assert.IsType<ToolCallTranscript>(await fixture.Session.Transcript.ReadToolCallTranscriptAsync(
        0, calls.ReturnedRequest, "returned", returned: true));

    Assert.EndsWith("returned result\n[image: image/jpeg]", transcript.Output);
  }

  private static TranscriptCalls SeedToolCalls(TemporaryDatabase database, Guid session,
      bool supportsImages = true, bool returnedImage = false, bool returnedClaimSent = true,
      bool returnedClaimCompacted = false) {
    Guid run = Guid.NewGuid();
    Guid directRequest = Guid.NewGuid(), runningRequest = Guid.NewGuid();
    Guid returnedRequest = Guid.NewGuid(), unicodeRequest = Guid.NewGuid();
    Guid completion = Guid.NewGuid();
    string directArguments = JsonSerializer.Serialize(new { path = "file.txt" });
    string runningArguments = JsonSerializer.Serialize(new { });
    string returnedArguments = JsonSerializer.Serialize(new { task = "long" });
    const string unicodeArguments = "{\"unicode\":\"😀\"}";
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
      INSERT INTO execution_requests (request_id, run_id, request_index, attempt, response_final, outcome)
      VALUES ('{directRequest:D}', '{run:D}', 0, 2, 1, 0),
          ('{runningRequest:D}', '{run:D}', 1, 2, 1, 0),
          ('{returnedRequest:D}', '{run:D}', 2, 2, 1, 0),
          ('{unicodeRequest:D}', '{run:D}', 3, {(returnedClaimSent ? 2 : 1)}, 1, 0);
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
    if (returnedClaimCompacted)
      SeedCompaction(database, session, run, unicodeRequest, unicodeRequest, boundaryRequestIndex: 3, firstKeptMessageIndex: 1);
    return new(directRequest, runningRequest, returnedRequest, unicodeRequest);
  }

  private static Guid SeedStoppedDirectResult(TemporaryDatabase database, Guid session, bool neverAttemptedContinuation,
      ReceiverCompaction receiverCompaction = ReceiverCompaction.None) {
    Guid originalRun = Guid.NewGuid(), nextRun = Guid.NewGuid(), laterRun = Guid.NewGuid();
    Guid originalInput = Guid.NewGuid(), nextInput = Guid.NewGuid(), laterInput = Guid.NewGuid();
    Guid directRequest = Guid.NewGuid(), continuationRequest = Guid.NewGuid(), receiverRequest = Guid.NewGuid();
    Guid laterRequest = Guid.NewGuid();
    string originalSelection = JsonSerializer.Serialize(TestLlmSelection.Default);
    string nextSelection = JsonSerializer.Serialize(TestLlmSelection.Default with { SupportsImages = false });
    string directArguments = JsonSerializer.Serialize(new { });
    const string directResult = "{\"text\":\"direct output\",\"images\":[{\"hash\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"mediaType\":\"image/png\",\"byteLength\":1}],\"details\":null,\"isError\":false}";
    database.Execute($"""
      INSERT INTO input_decisions
          (session_id, input_id, expected_history_revision, original_text_utf8, decision, run_id, llm_selection_json)
      VALUES ('{session:D}', '{originalInput:D}', 0, X'', 1, '{originalRun:D}', '{originalSelection}'),
          ('{session:D}', '{nextInput:D}', 0, X'', 1, '{nextRun:D}', '{nextSelection}');
      INSERT INTO execution_runs (session_id, run_id, history_revision, input_id, stopping, outcome)
      VALUES ('{session:D}', '{originalRun:D}', 0, '{originalInput:D}', 1, 1),
          ('{session:D}', '{nextRun:D}', 0, '{nextInput:D}', 0, NULL);
      INSERT INTO execution_requests (request_id, run_id, request_index, attempt, response_final, outcome)
      VALUES ('{directRequest:D}', '{originalRun:D}', 0, 2, 1, 0),
          ('{receiverRequest:D}', '{nextRun:D}', 0, 2, 0, NULL);
      INSERT INTO execution_tools
          (run_id, request_id, call_index, call_id, name, arguments_json, started, outcome, result_json)
      VALUES ('{originalRun:D}', '{directRequest:D}', 0, 'direct', 'read', '{directArguments}', 1, 0, '{directResult}');
      """);
    if (neverAttemptedContinuation)
      database.Execute($"""
        INSERT INTO execution_requests (request_id, run_id, request_index, attempt, response_final, outcome)
        VALUES ('{continuationRequest:D}', '{originalRun:D}', 1, 1, 1, 1);
        """);

    if (receiverCompaction == ReceiverCompaction.AfterReceiver) {
      database.Execute($"""
        UPDATE execution_requests SET response_final = 1, outcome = 0 WHERE request_id = '{receiverRequest:D}';
        UPDATE execution_runs SET outcome = 0 WHERE run_id = '{nextRun:D}';
        INSERT INTO input_decisions
            (session_id, input_id, expected_history_revision, original_text_utf8, decision, run_id, llm_selection_json)
        VALUES ('{session:D}', '{laterInput:D}', 0, X'', 1, '{laterRun:D}', '{nextSelection}');
        INSERT INTO execution_runs (session_id, run_id, history_revision, input_id)
        VALUES ('{session:D}', '{laterRun:D}', 0, '{laterInput:D}');
        INSERT INTO execution_requests (request_id, run_id, request_index, attempt, response_final, outcome)
        VALUES ('{laterRequest:D}', '{laterRun:D}', 0, 2, 0, NULL);
        """);
      SeedCompaction(database, session, laterRun, laterRequest, laterRequest);
    } else if (receiverCompaction != ReceiverCompaction.None) {
      Guid boundaryRun = receiverCompaction == ReceiverCompaction.AtSource ? originalRun : nextRun;
      Guid boundaryRequest = receiverCompaction == ReceiverCompaction.AtSource ? directRequest : receiverRequest;
      SeedCompaction(database, session, boundaryRun, boundaryRequest, receiverRequest);
    }
    return directRequest;
  }

  private static void SeedCompaction(TemporaryDatabase database, Guid session, Guid boundaryRun,
      Guid boundaryRequest, Guid receivingRequest, int boundaryRequestIndex = 0, int firstKeptMessageIndex = 0) {
    Guid compaction = Guid.NewGuid();
    database.Execute($"""
      INSERT INTO session_compactions
          (compaction_id, session_id, history_revision, run_id, first_kept_request_id, first_kept_message_index,
              first_kept_run_ordinal, first_kept_request_index, reason, tokens_before, tokens_after, created_at, summary)
      VALUES ('{compaction:D}', '{session:D}', 0, '{boundaryRun:D}', '{boundaryRequest:D}', {firstKeptMessageIndex},
          (SELECT ordinal FROM execution_runs WHERE run_id = '{boundaryRun:D}'), {boundaryRequestIndex}, 0, 2, 1,
          '2026-11-12T00:00:00+00:00', 'fixture summary');
      UPDATE execution_requests SET compaction_id = '{compaction:D}' WHERE request_id = '{receivingRequest:D}';
      """);
  }

  private enum ReceiverCompaction { None, AtReceiver, AtSource, AfterReceiver }

  private sealed record TranscriptCalls(Guid DirectRequest, Guid RunningRequest, Guid ReturnedRequest, Guid UnicodeRequest);
}
