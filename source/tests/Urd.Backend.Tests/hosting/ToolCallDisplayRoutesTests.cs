using System.Net;
using System.Text.Json;
using Urd.Backend.Tests.Support;
using Urd.Core.Sessions;
using Urd.Core.Storage;
using Urd.Server;
using Xunit;
using static Urd.Backend.Tests.Support.HttpTest;

namespace Urd.Backend.Tests.Hosting;

public sealed class ToolCallDisplayRoutesTests {
  [Fact]
  public async Task HistoryAndPositionWindowsExposeDeduplicatedLabelsWithoutExcerpts() {
    using var database = new TemporaryDatabase();
    SeedToolCall(database, out Guid sessionId, out Guid runId, out Guid requestId,
        out string callId, out Guid startEntryId, out Guid promptEntryId, out _, out _);
    await using var server = new RunningServer(Options(database));
    await server.WaitForReadyAsync();

    using (HttpResponseMessage history = await RequestAsync(server.Client, HttpMethod.Get,
        $"/api/sessions/{sessionId}/history?mode=latest&expectedHistoryRevision=0")) {
      Assert.Equal(HttpStatusCode.OK, history.StatusCode);
      AssertPrivateResponse(history);
      string text = await history.Content.ReadAsStringAsync();
      using JsonDocument body = JsonDocument.Parse(text);
      AssertLabel(body.RootElement.GetProperty("toolLabels"), requestId, callId);
      Assert.DoesNotContain("argumentsExcerpt", text, StringComparison.OrdinalIgnoreCase);
      Assert.DoesNotContain("do-not-send", text, StringComparison.Ordinal);
    }

    using (HttpResponseMessage located = await RequestAsync(server.Client, HttpMethod.Get,
        $"/api/sessions/{sessionId}/positions/locate?expectedHistoryRevision=0&fraction=1")) {
      Assert.Equal(HttpStatusCode.OK, located.StatusCode);
      AssertPrivateResponse(located);
      string text = await located.Content.ReadAsStringAsync();
      using JsonDocument body = JsonDocument.Parse(text);
      AssertLabel(body.RootElement.GetProperty("toolLabels"), requestId, callId);
      Assert.Equal(3, body.RootElement.GetProperty("entries").GetArrayLength());
      Assert.DoesNotContain("do-not-send", text, StringComparison.Ordinal);
    }

    using (HttpResponseMessage entryPosition = await RequestAsync(server.Client, HttpMethod.Get,
        $"/api/sessions/{sessionId}/positions/entries/{runId}/{startEntryId}?expectedHistoryRevision=0&kind=tool")) {
      Assert.Equal(HttpStatusCode.OK, entryPosition.StatusCode);
      using JsonDocument body = JsonDocument.Parse(await entryPosition.Content.ReadAsStringAsync());
      AssertLabel(body.RootElement.GetProperty("toolLabels"), requestId, callId);
    }

    using (HttpResponseMessage activated = await RequestAsync(server.Client, HttpMethod.Get,
        $"/api/sessions/{sessionId}/positions/prompts/{runId}/{promptEntryId}?expectedHistoryRevision=0")) {
      Assert.Equal(HttpStatusCode.OK, activated.StatusCode);
      using JsonDocument body = JsonDocument.Parse(await activated.Content.ReadAsStringAsync());
      AssertLabel(body.RootElement.GetProperty("toolLabels"), requestId, callId);
    }
  }

  [Fact]
  public async Task ToolCallTranscriptReturnsFoundMissingAndBoundedPreviewResponses() {
    using var database = new TemporaryDatabase();
    SeedToolCall(database, out Guid sessionId, out _, out Guid requestId, out string callId,
        out _, out _, out _, out int outputLength);
    await using var server = new RunningServer(Options(database));
    await server.WaitForReadyAsync();
    string endpoint = $"/api/sessions/{sessionId}/tool-calls/{requestId}/{Uri.EscapeDataString(callId)}";

    using (HttpResponseMessage preview = await RequestAsync(server.Client, HttpMethod.Get,
        endpoint + "?expectedHistoryRevision=0&returned=true&full=false")) {
      Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
      AssertPrivateResponse(preview);
      using JsonDocument body = JsonDocument.Parse(await preview.Content.ReadAsStringAsync());
      JsonElement transcript = body.RootElement;
      Assert.Equal("{\"path\":\"file.txt\"}", transcript.GetProperty("input").GetString());
      Assert.Equal(65536, transcript.GetProperty("output").GetString()!.Length);
      Assert.True(transcript.GetProperty("truncated").GetBoolean());
    }

    using (HttpResponseMessage full = await RequestAsync(server.Client, HttpMethod.Get,
        endpoint + "?expectedHistoryRevision=0&returned=true&full=true")) {
      Assert.Equal(HttpStatusCode.OK, full.StatusCode);
      using JsonDocument body = JsonDocument.Parse(await full.Content.ReadAsStringAsync());
      Assert.True(body.RootElement.GetProperty("output").GetString()!.Length > outputLength);
      Assert.False(body.RootElement.GetProperty("truncated").GetBoolean());
    }

    using (HttpResponseMessage missing = await RequestAsync(server.Client, HttpMethod.Get,
        $"/api/sessions/{sessionId}/tool-calls/{requestId}/missing?expectedHistoryRevision=0&returned=false&full=false"))
      Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    using (HttpResponseMessage stale = await RequestAsync(server.Client, HttpMethod.Get,
        endpoint + "?expectedHistoryRevision=1&returned=true&full=false"))
      Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
  }

  [Fact]
  public async Task RevealRejectsRelativeAndMissingPathsWithoutLaunchingExplorer() {
    using var database = new TemporaryDatabase();
    await using var server = new RunningServer(Options(database));
    await server.WaitForReadyAsync();

    using (HttpResponseMessage relative = await RequestAsync(server.Client, HttpMethod.Post,
        "/api/files/reveal", JsonBody("{\"path\":\"relative/file.txt\"}")))
      Assert.Equal(HttpStatusCode.BadRequest, relative.StatusCode);

    string missingPath = Path.Combine(Path.GetTempPath(), $"urd-no-such-file-{Guid.NewGuid():N}");
    using (HttpResponseMessage missing = await RequestAsync(server.Client, HttpMethod.Post,
        "/api/files/reveal", JsonBody(JsonSerializer.Serialize(new { path = missingPath }))))
      Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
  }

  [Theory]
  [InlineData("file.txt")]
  [InlineData("folder, with spaces/reveal, unicode-Ω & %TEST%.txt")]
  public void ExplorerSelectionPreservesNativeQuotingWithoutAShell(string relativePath) {
    string path = Path.GetFullPath(Path.Combine(Path.GetTempPath(), relativePath));
    var start = FileRoutes.ExplorerSelection(path);

    Assert.Equal(Path.GetFullPath(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe")), start.FileName);
    Assert.Equal($"/select,\"{path}\"", start.Arguments);
    Assert.Empty(start.ArgumentList);
    Assert.False(start.UseShellExecute);
  }

  private static void AssertLabel(JsonElement labels, Guid requestId, string callId) {
    Assert.Single(labels.EnumerateObject());
    JsonElement label = labels.GetProperty($"{requestId:D}/{callId}");
    Assert.Equal("read", label.GetProperty("icon").GetString());
    Assert.Equal("file.txt", label.GetProperty("title").GetString());
    Assert.NotNull(label.GetProperty("path").GetString());
  }

  private static void SeedToolCall(TemporaryDatabase database, out Guid sessionId, out Guid runId,
      out Guid requestId, out string callId, out Guid startEntryId, out Guid promptEntryId,
      out Guid completionEntryId, out int outputLength) {
    sessionId = Guid.NewGuid();
    runId = Guid.NewGuid();
    requestId = Guid.NewGuid();
    callId = "tool-call";
    startEntryId = Guid.NewGuid();
    promptEntryId = Guid.NewGuid();
    completionEntryId = Guid.NewGuid();
    const int resultTextLength = 70000;
    outputLength = resultTextLength;
    string workingDirectory = Path.Combine(Path.GetTempPath(), "urd-tool-call-routes");
    string arguments = "{\"path\":\"file.txt\"}";
    string excerpt = "{\"path\":\"file.txt\",\"private\":\"do-not-send\"}";
    string result = JsonSerializer.Serialize(new {
      text = new string('x', resultTextLength), images = Array.Empty<object>(), details = (string?)null, isError = false
    });
    using (UrdStore store = UrdStore.Open(database.Path))
      store.Sessions.CreatePrimary(sessionId, workingDirectory);

    database.Execute($"""
      INSERT INTO execution_runs (session_id, run_id, history_revision, compaction_selection_json, outcome)
      VALUES ('{sessionId:D}', '{runId:D}', 0, 'test', 0);
      INSERT INTO execution_requests (request_id, run_id, request_index, response_final, outcome)
      VALUES ('{requestId:D}', '{runId:D}', 0, 1, 0);
      INSERT INTO input_decisions
          (session_id, input_id, expected_history_revision, original_text_utf8, decision, run_id, llm_selection_json)
      VALUES ('{sessionId:D}', '{promptEntryId:D}', 0, X'70', 1, '{runId:D}', 'test');
      INSERT INTO execution_messages
          (session_id, history_revision, run_id, role, message_id, input_id, request_id)
      VALUES ('{sessionId:D}', 0, '{runId:D}', 0, '{promptEntryId:D}', '{promptEntryId:D}', '{requestId:D}');
      INSERT INTO execution_tools
          (run_id, request_id, call_index, call_id, name, arguments_json, arguments_excerpt,
           started, background, outcome, result_json, started_at, ended_at)
      VALUES ('{runId:D}', '{requestId:D}', 0, '{callId}', 'read', {Sql(arguments)}, {Sql(excerpt)},
          1, 1, 0, {Sql(result)}, '2026-11-12T00:00:00+00:00', '2026-11-12T00:00:01+00:00');
      INSERT INTO execution_messages (session_id, history_revision, run_id, role, message_id, request_id, call_id, tool_name)
      VALUES ('{sessionId:D}', 0, '{runId:D}', 2, '{startEntryId:D}', '{requestId:D}', '{callId}', 'read'),
          ('{sessionId:D}', 0, '{runId:D}', 3, '{completionEntryId:D}', '{requestId:D}', '{callId}', 'read');
      INSERT INTO execution_tool_completions
          (completion_id, session_id, history_revision, request_id, call_id, created_at, published)
      VALUES ('{completionEntryId:D}', '{sessionId:D}', 0, '{requestId:D}', '{callId}',
          '2026-11-12T00:00:01+00:00', 1);
      """);
  }

  private static string Sql(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

  private static ServerOptions Options(TemporaryDatabase database) => new(Path.GetDirectoryName(database.Path)!, 0);
}
