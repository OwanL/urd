using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Urd.Backend.Tests.Support;
using Urd.Core.Execution;
using Urd.Core.MarkdownRendering;
using Urd.Core.Sessions;
using Urd.Core.Storage;
using Urd.Server;
using Xunit;
using static Urd.Backend.Tests.Support.HttpTest;
using static Urd.Backend.Tests.Support.TestAsync;

namespace Urd.Backend.Tests.Hosting;

public sealed class MarkdownRoutesTests {
  private static readonly JsonSerializerOptions inputJson = new(JsonSerializerDefaults.Web) {
    Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
  };

  [Fact]
  public async Task PageOwnedRoutesPreserveBoundsWireTypesAndReleaseOnDisconnect() {
    using var database = new TemporaryDatabase();
    (Guid sessionId, TranscriptEntryRef entry) = Seed(database);
    await using var server = new RunningServer(RunningServer.OptionsFor(database),
        createMarkdown: () => new(new(2, 4 * 1024 * 1024, 8, 1, 4096)));
    await server.WaitForReadyAsync();
    using HttpResponseMessage stream = await OpenStreamAsync(server.Client);
    Guid streamId = await ReadStreamIdAsync(stream);
    string path = $"/api/events/{streamId}/markdown/{Guid.NewGuid()}";
    string other = $"/api/events/{streamId}/markdown/{Guid.NewGuid()}";
    string body = JsonSerializer.Serialize(new { sessionId, entry }, inputJson);

    using (HttpResponseMessage denied = await RequestAsync(server.Client, HttpMethod.Post,
        path, JsonBody(body), marker: false)) {
      Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
      AssertPrivateResponse(denied);
    }
    JsonElement opened = await PostJsonAsync(server.Client, path, body);
    Guid generation = opened.GetProperty("manifest").GetProperty("generation").GetGuid();
    Assert.Equal("user", opened.GetProperty("source").GetProperty("from")
        .GetProperty("message").GetProperty("role").GetString());
    await AssertStatusAsync(server.Client, HttpMethod.Post, path, HttpStatusCode.Conflict, body);
    await AssertStatusAsync(server.Client, HttpMethod.Post, other, HttpStatusCode.ServiceUnavailable, body);

    string cursor = $"?generation={generation}&index=0";
    using (HttpResponseMessage piecesResponse = await RequestAsync(server.Client, HttpMethod.Get, path + "/pieces" + cursor)) {
      byte[] bytes = await piecesResponse.Content.ReadAsByteArrayAsync();
      Assert.Equal(HttpStatusCode.OK, piecesResponse.StatusCode);
      Assert.InRange(bytes.Length, 1, 16 * 1024);
      using JsonDocument wire = JsonDocument.Parse(bytes);
      Assert.Equal(JsonValueKind.Number, wire.RootElement.GetProperty("pieces")[0].GetProperty("kind").ValueKind);
      MarkdownPiecePage pieces = JsonSerializer.Deserialize<MarkdownPiecePage>(bytes, MarkdownRenderJson.Options)!;
      Assert.NotEmpty(pieces.Pieces);
      int contextId = pieces.Pieces[0].Context;
      using HttpResponseMessage contextResponse = await RequestAsync(server.Client, HttpMethod.Get,
          path + $"/contexts/{contextId}" + cursor);
      MarkdownContextPage context = (await contextResponse.Content.ReadFromJsonAsync<MarkdownContextPage>(MarkdownRenderJson.Options))!;
      Assert.Equal(MarkdownElementKind.Link, context.Context.Kind);
      Assert.Equal("https://example.invalid/0", context.Values.Single().Text);
      Assert.NotEqual(-1, context.Context.Parent);
      using HttpResponseMessage parentContextResponse = await RequestAsync(server.Client, HttpMethod.Get,
          path + $"/contexts/{context.Context.Parent}" + cursor);
      MarkdownContextPage parentContext = (await parentContextResponse.Content.ReadFromJsonAsync<MarkdownContextPage>(MarkdownRenderJson.Options))!;
      Assert.Equal(context.Context.Id, parentContext.Context.FirstChild);
      using HttpResponseMessage childLocationResponse = await RequestAsync(server.Client, HttpMethod.Get,
          path + $"/contexts/{parentContext.Context.Id}/children/0");
      MarkdownRenderCursor childLocation = await childLocationResponse.Content.ReadFromJsonAsync<MarkdownRenderCursor>(MarkdownRenderJson.Options);
      Assert.Equal(new MarkdownRenderCursor(generation, context.Context.FirstPiece), childLocation);
      using HttpResponseMessage beforeResponse = await RequestAsync(server.Client, HttpMethod.Get,
          path + $"/pieces/before?generation={generation}&index={pieces.Next}");
      MarkdownPiecePage before = (await beforeResponse.Content.ReadFromJsonAsync<MarkdownPiecePage>(MarkdownRenderJson.Options))!;
      Assert.Equal(pieces.Next, before.Next);
      Assert.NotEmpty(before.Pieces);
    }

    int[] ids = Enumerable.Range(0, 256).ToArray();
    int next = 0, pages = 0;
    do {
      string batch = JsonSerializer.Serialize(new { generation, index = next, ids });
      using HttpResponseMessage response = await RequestAsync(server.Client, HttpMethod.Post, path + "/contexts", JsonBody(batch));
      Assert.Equal(HttpStatusCode.OK, response.StatusCode);
      byte[] bytes = await response.Content.ReadAsByteArrayAsync();
      Assert.InRange(bytes.Length, 1, 16 * 1024);
      MarkdownContextDescriptorPage page = JsonSerializer.Deserialize<MarkdownContextDescriptorPage>(bytes, MarkdownRenderJson.Options)!;
      Assert.Equal(ids.Skip(next).Take(page.Contexts.Count), page.Contexts.Select(context => context.Id));
      Assert.True(page.Next > next);
      next = page.Next;
      pages++;
      Assert.Equal(next < ids.Length, page.HasMore);
    } while (next < ids.Length);
    Assert.True(pages > 1);
    using (HttpResponseMessage located = await RequestAsync(server.Client, HttpMethod.Get, path + "/locate?sourceUtf8Offset=0")) {
      MarkdownRenderCursor location = await located.Content.ReadFromJsonAsync<MarkdownRenderCursor>(MarkdownRenderJson.Options);
      Assert.Equal(new(generation, 0), location);
    }
    using (HttpResponseMessage sourceResponse = await RequestAsync(server.Client, HttpMethod.Get,
        path + "/source?sourceUtf8Offset=1")) {
      MessageContentCursor saved = (await sourceResponse.Content.ReadFromJsonAsync<MessageContentCursor>(inputJson))!;
      Assert.Equal(entry.EntryId, saved.Message.MessageId);
      Assert.Equal((0L, 1), (saved.SegmentIndex, saved.Utf8Offset));
      string savedJson = JsonSerializer.Serialize(saved, inputJson);
      JsonElement location = await PostJsonAsync(server.Client, path + "/locate", savedJson);
      Assert.Equal(generation, location.GetProperty("generation").GetGuid());
      Assert.Equal(0, location.GetProperty("index").GetInt32());
      await AssertStatusAsync(server.Client, HttpMethod.Post, path + "/locate", HttpStatusCode.BadRequest,
          JsonSerializer.Serialize(saved with { Message = saved.Message with { HistoryRevision = 1 } }, inputJson));
    }
    await AssertStatusAsync(server.Client, HttpMethod.Get,
        path + $"/pieces?generation={Guid.NewGuid()}&index=0", HttpStatusCode.BadRequest);
    await AssertStatusAsync(server.Client, HttpMethod.Post, path + "/contexts", HttpStatusCode.BadRequest,
        JsonSerializer.Serialize(new { generation, index = 0, ids = new[] { 0, 0 } }));
    await AssertStatusAsync(server.Client, HttpMethod.Post, path + "/release", HttpStatusCode.NoContent, "{}");
    await AssertStatusAsync(server.Client, HttpMethod.Post, path + "/release", HttpStatusCode.NoContent, "{}");
    await AssertStatusAsync(server.Client, HttpMethod.Get, path + "/pieces" + cursor, HttpStatusCode.Gone);
    Assert.Equal(generation, (await PostJsonAsync(server.Client, other, body))
        .GetProperty("manifest").GetProperty("generation").GetGuid());

    stream.Dispose();
    using var deadline = new CancellationTokenSource(TestTimeout);
    while (true) {
      using HttpResponseMessage response = await RequestAsync(server.Client, HttpMethod.Get, other + "/pieces" + cursor);
      if (response.StatusCode == HttpStatusCode.NotFound) break;
      Assert.Equal(HttpStatusCode.OK, response.StatusCode);
      await Task.Delay(10, deadline.Token);
    }
    using HttpResponseMessage replacementStream = await OpenStreamAsync(server.Client);
    Guid replacementId = await ReadStreamIdAsync(replacementStream);
    string replacement = $"/api/events/{replacementId}/markdown/{Guid.NewGuid()}";
    // The single lease slot is available again without an explicit release from the disconnected page.
    await PostJsonAsync(server.Client, replacement, body);
    await AssertStatusAsync(server.Client, HttpMethod.Post, replacement + "/release", HttpStatusCode.NoContent, "{}");
    database.ForceHistoryRevision(1);
    await AssertStatusAsync(server.Client, HttpMethod.Post,
        $"/api/events/{replacementId}/markdown/{Guid.NewGuid()}", HttpStatusCode.Conflict, body);
  }

  [Fact]
  public async Task RetainedChildMarkdownUsesReadOnlyTranscript() {
    using var database = new TemporaryDatabase();
    (Guid sessionId, TranscriptEntryRef entry) = Seed(database, child: true);
    await using var server = new RunningServer(RunningServer.OptionsFor(database));
    await server.WaitForReadyAsync();
    using var stream = await OpenStreamAsync(server.Client);
    Guid streamId = await ReadStreamIdAsync(stream);
    string PathForLease() => $"/api/events/{streamId}/markdown/{Guid.NewGuid()}";
    string body = JsonSerializer.Serialize(new { sessionId, entry }, inputJson);
    string path = PathForLease();
    JsonElement opened = await PostJsonAsync(server.Client, path, body);
    Guid generation = opened.GetProperty("manifest").GetProperty("generation").GetGuid();
    await AssertStatusAsync(server.Client, HttpMethod.Get,
        $"{path}/pieces?generation={generation}&index=0", HttpStatusCode.OK);
    await AssertStatusAsync(server.Client, HttpMethod.Post, $"/api/sessions/{sessionId}/close", HttpStatusCode.NotFound, "{}");
    await AssertStatusAsync(server.Client, HttpMethod.Post, PathForLease(), HttpStatusCode.NotFound,
        JsonSerializer.Serialize(new { sessionId = Guid.NewGuid(), entry }, inputJson));
    await AssertStatusAsync(server.Client, HttpMethod.Post, PathForLease(), HttpStatusCode.NotFound,
        JsonSerializer.Serialize(new { sessionId, entry = entry with { EntryId = Guid.NewGuid() } }, inputJson));
    database.ForceHistoryRevision(1);
    await AssertStatusAsync(server.Client, HttpMethod.Post, PathForLease(), HttpStatusCode.Conflict, body);
  }

  [Theory]
  [InlineData(1)]
  [InlineData(2)]
  public async Task ClosingParentRevokesRetainedChildHistory(int depth) {
    using var database = new TemporaryDatabase();
    (Guid sessionId, TranscriptEntryRef entry) = Seed(database, child: true, depth: depth);
    Guid parentId = sessionId;
    using (UrdStore store = UrdStore.Open(database.Path)) {
      for (int i = 0; i < depth; i++)
        parentId = store.Sessions.Read(parentId)!.ParentSessionId!.Value;
    }
    await using var server = new RunningServer(RunningServer.OptionsFor(database));
    await server.WaitForReadyAsync();
    using var stream = await OpenStreamAsync(server.Client);
    Guid streamId = await ReadStreamIdAsync(stream);
    string path = $"/api/events/{streamId}/markdown/{Guid.NewGuid()}";
    JsonElement lease = await PostJsonAsync(server.Client, path, JsonSerializer.Serialize(new { sessionId, entry }, inputJson));
    Guid generation = lease.GetProperty("manifest").GetProperty("generation").GetGuid();
    string pieces = $"{path}/pieces?generation={generation}&index=0";
    await AssertStatusAsync(server.Client, HttpMethod.Get, pieces, HttpStatusCode.OK);
    await AssertStatusAsync(server.Client, HttpMethod.Post, $"/api/sessions/{parentId}/close", HttpStatusCode.NoContent, "{}");
    await AssertStatusAsync(server.Client, HttpMethod.Get, pieces, HttpStatusCode.Gone);
  }

  [Fact]
  public async Task ClosingSessionRevokesHistoryAlreadyOpenOnAnotherPage() {
    using var database = new TemporaryDatabase();
    (Guid sessionId, TranscriptEntryRef entry) = Seed(database);
    await using var server = new RunningServer(RunningServer.OptionsFor(database));
    await server.WaitForReadyAsync();
    using var stream = await OpenStreamAsync(server.Client);
    Guid streamId = await ReadStreamIdAsync(stream);
    string path = $"/api/events/{streamId}/markdown/{Guid.NewGuid()}";
    JsonElement lease = await PostJsonAsync(server.Client, path, JsonSerializer.Serialize(new { sessionId, entry }, inputJson));
    Guid generation = lease.GetProperty("manifest").GetProperty("generation").GetGuid();
    string pieces = $"{path}/pieces?generation={generation}&index=0";
    await AssertStatusAsync(server.Client, HttpMethod.Get, pieces, HttpStatusCode.OK);
    await AssertStatusAsync(server.Client, HttpMethod.Post, $"/api/sessions/{sessionId}/close", HttpStatusCode.NoContent, "{}");
    await AssertStatusAsync(server.Client, HttpMethod.Get, pieces, HttpStatusCode.Gone);
  }

  [Fact]
  public async Task ManagedImageBytesArePrivateAndScopedToTheOwningMessage() {
    using var database = new TemporaryDatabase();
    byte[] png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aXioAAAAASUVORK5CYII=");
    string path = database.Path + ".png";
    await File.WriteAllBytesAsync(path, png);
    string embed = $"![Before](<{path.Replace('\\', '/')}>)";
    var target = Assert.Single(MarkdownParser.ReadImageTargets(embed));
    var messageId = Guid.NewGuid();
    Guid sessionId, runId;
    try {
      using UrdStore store = UrdStore.Open(database.Path);
      sessionId = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
      runId = Guid.NewGuid();
      var identity = new ExecutionIdentity(sessionId, runId, 0);
      store.UserMessages.Submit(sessionId, 0, new(Guid.NewGuid(), 0, "show screenshot", TestLlmSelection.Default), true, runId);
      var request = store.Executions.CreateNextRequest(identity)!;
      Assert.True(store.Executions.StartRequest(identity, request.RequestId));
      Assert.True(store.LlmResponses.Append(identity, new(request.RequestId, messageId, 0, embed, false)));
      store.MarkdownImageFiles.SaveImage(identity, messageId, target, null);
    } finally {
      File.Delete(path);
    }

    string mediaDirectory = database.Path + ".markdown-images";
    string orphan = Path.Combine(mediaDirectory, new string('0', 64));
    string unrelated = Path.Combine(mediaDirectory, "notes.txt");
    await File.WriteAllBytesAsync(orphan, png);
    await File.WriteAllTextAsync(unrelated, "leave this alone");

    await using var server = new RunningServer(RunningServer.OptionsFor(database));
    await server.WaitForReadyAsync();
    Assert.False(File.Exists(orphan));
    Assert.True(File.Exists(unrelated));
    string Url(Guid entryId, int revision = 0) =>
        $"/api/sessions/{sessionId}/markdown-images/{runId}/{entryId}/{target.Key}?historyRevision={revision}";
    using var denied = await RequestAsync(server.Client, HttpMethod.Get, Url(messageId), marker: false);
    Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    using var found = await RequestAsync(server.Client, HttpMethod.Get, Url(messageId));
    Assert.Equal(HttpStatusCode.OK, found.StatusCode);
    AssertPrivateResponse(found);
    Assert.Equal("image/png", found.Content.Headers.ContentType?.MediaType);
    Assert.Equal(png, await found.Content.ReadAsByteArrayAsync());
    await AssertStatusAsync(server.Client, HttpMethod.Get, Url(Guid.NewGuid()), HttpStatusCode.NotFound);
    await AssertStatusAsync(server.Client, HttpMethod.Get, Url(messageId, revision: 1), HttpStatusCode.NotFound);
    await AssertStatusAsync(server.Client, HttpMethod.Get, Url(messageId).Replace(target.Key, "invalid"), HttpStatusCode.BadRequest);
  }

  [Theory]
  [InlineData(false, 410)]
  [InlineData(true, 503)]
  public async Task PageCancellationMapsToExpiryOrShutdownButPeerAbortPropagates(bool stopping, int status) {
    using ServiceProvider services = new ServiceCollection()
        .AddSingleton<IHostApplicationLifetime>(new TestLifetime(stopping)).BuildServiceProvider();
    var http = new DefaultHttpContext { RequestServices = services };
    http.Request.Method = HttpMethods.Get;
    var invocation = new DefaultEndpointFilterInvocationContext(http);
    static ValueTask<object?> Cancel(EndpointFilterInvocationContext _) =>
        ValueTask.FromException<object?>(new OperationCanceledException());
    object? result = await MarkdownRoutes.HandleRequestAsync(invocation, Cancel);
    Assert.Equal(status, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
    http.RequestAborted = new CancellationToken(canceled: true);
    await Assert.ThrowsAsync<OperationCanceledException>(() =>
        MarkdownRoutes.HandleRequestAsync(invocation, Cancel).AsTask());
  }

  private sealed class TestLifetime(bool stopping) : IHostApplicationLifetime {
    public CancellationToken ApplicationStarted => CancellationToken.None;
    public CancellationToken ApplicationStopping => new(stopping);
    public CancellationToken ApplicationStopped => CancellationToken.None;
    public void StopApplication() => throw new NotSupportedException();
  }

  private static (Guid SessionId, TranscriptEntryRef Entry) Seed(TemporaryDatabase database, bool child = false, int depth = 1) {
    using UrdStore store = UrdStore.Open(database.Path);
    Guid session = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
    for (int i = 0; child && i < depth; i++)
      session = store.Sessions.CreateSubagent(Guid.NewGuid(), session, "spawn").Id;
    Guid run = Guid.NewGuid();
    var identity = new ExecutionIdentity(session, run, 0);
    string text = string.Join("\n\n", Enumerable.Range(0, 130).Select(i => $"[item{i}](https://example.invalid/{i})"));
    store.UserMessages.Submit(session, 0, new(Guid.NewGuid(), 0, text, TestLlmSelection.Default), true, run);
    var request = store.Executions.CreateNextRequest(identity)!;
    Assert.True(store.Executions.StartRequest(identity, request.RequestId));
    return (session, store.Positions.Locate(session, 0, 0).Entries.Single().Descriptor.Entry);
  }

  private static async Task<JsonElement> PostJsonAsync(HttpClient client, string path, string body) {
    using HttpResponseMessage response = await RequestAsync(client, HttpMethod.Post, path, JsonBody(body));
    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    AssertPrivateResponse(response);
    return await response.Content.ReadFromJsonAsync<JsonElement>();
  }

  private static async Task AssertStatusAsync(HttpClient client, HttpMethod method, string path,
      HttpStatusCode expected, string? body = null) {
    using HttpResponseMessage response = await RequestAsync(client, method, path, body is null ? null : JsonBody(body));
    Assert.Equal(expected, response.StatusCode);
  }

  private static Task<HttpResponseMessage> OpenStreamAsync(HttpClient client) =>
      RequestAsync(client, HttpMethod.Get, "/api/events", completionOption: HttpCompletionOption.ResponseHeadersRead);

  private static async Task<Guid> ReadStreamIdAsync(HttpResponseMessage response) {
    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(), leaveOpen: true);
    Assert.Equal("event: connected", await reader.ReadLineAsync().WaitAsync(TestTimeout));
    string line = (await reader.ReadLineAsync().WaitAsync(TestTimeout))!;
    Assert.StartsWith("data: ", line, StringComparison.Ordinal);
    using JsonDocument connected = JsonDocument.Parse(line[6..]);
    return connected.RootElement.GetProperty("stream").GetGuid();
  }
}
