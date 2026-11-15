using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Urd.Backend.Tests.Support;
using Urd.Core.Sessions;
using Xunit;
using static Urd.Backend.Tests.Support.HttpTest;

namespace Urd.Backend.Tests.Hosting;

public sealed class PreferencesRoutesTests {
  [Fact]
  public async Task WorkingDirectoryPreferenceIsValidatedAndStampedOnNewSessionSnapshots() {
    using var database = new TemporaryDatabase();
    string workingDirectory = Path.Combine(Path.GetDirectoryName(database.Path)!, "preferred-directory");
    Directory.CreateDirectory(workingDirectory);
    await using var server = new RunningServer(RunningServer.OptionsFor(database));
    await server.WaitForReadyAsync();

    using (HttpResponseMessage initial = await RequestAsync(server.Client, HttpMethod.Get, "/api/preferences")) {
      Assert.Equal(HttpStatusCode.OK, initial.StatusCode);
      AssertPrivateResponse(initial);
      JsonElement preferences = await initial.Content.ReadFromJsonAsync<JsonElement>();
      Assert.Equal(Path.GetPathRoot(Environment.CurrentDirectory), preferences.GetProperty("workingDirectory").GetString());
      Assert.Equal(JsonValueKind.Null, preferences.GetProperty("compactionTriggerTokenCount").ValueKind);
    }

    using (HttpResponseMessage invalid = await RequestAsync(server.Client, HttpMethod.Post,
        "/api/preferences/working-directory", JsonBody("{\"workingDirectory\":\"relative\"}"))) {
      Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
      AssertPrivateResponse(invalid);
      using JsonDocument error = JsonDocument.Parse(await invalid.Content.ReadAsStringAsync());
      Assert.Equal("Working directory must be an existing, fully qualified directory.",
          error.RootElement.GetProperty("error").GetString());
    }

    using (HttpResponseMessage saved = await RequestAsync(server.Client, HttpMethod.Post,
        "/api/preferences/working-directory", JsonBody(JsonSerializer.Serialize(new { workingDirectory })))) {
      Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
      AssertPrivateResponse(saved);
      Assert.Equal(workingDirectory,
          (await saved.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("workingDirectory").GetString());
    }

    using HttpResponseMessage created = await RequestAsync(server.Client, HttpMethod.Post, "/api/sessions", JsonBody("{}"));
    Assert.Equal(HttpStatusCode.Created, created.StatusCode);
    Guid sessionId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("sessionId").GetGuid();
    using HttpResponseMessage snapshotResponse = await RequestAsync(server.Client, HttpMethod.Get,
        $"/api/sessions/{sessionId}/snapshot");
    Assert.Equal(HttpStatusCode.OK, snapshotResponse.StatusCode);
    SessionSnapshot snapshot = (await snapshotResponse.Content.ReadFromJsonAsync<SessionSnapshot>())!;
    Assert.Equal(workingDirectory, snapshot.WorkingDirectory);
  }

  [Fact]
  public async Task CompactionTriggerPreferenceValidatesInputAndReturnsFullPreferences() {
    using var database = new TemporaryDatabase();
    string dataDirectory = Path.GetDirectoryName(database.Path)!;
    string workingDirectory = Path.Combine(dataDirectory, "compaction-preference-directory");
    Directory.CreateDirectory(workingDirectory);
    await using var server = new RunningServer(RunningServer.OptionsFor(database));
    await server.WaitForReadyAsync();

    using (HttpResponseMessage workingDirectoryResponse = await RequestAsync(server.Client, HttpMethod.Post,
        "/api/preferences/working-directory", JsonBody(JsonSerializer.Serialize(new { workingDirectory }))))
      Assert.Equal(HttpStatusCode.OK, workingDirectoryResponse.StatusCode);

    using (HttpResponseMessage saved = await RequestAsync(server.Client, HttpMethod.Post,
        "/api/preferences/compaction-trigger", JsonBody("{\"compactionTriggerTokenCount\":32768}"))) {
      Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
      AssertPrivateResponse(saved);
      JsonElement preferences = await saved.Content.ReadFromJsonAsync<JsonElement>();
      Assert.Equal(32768, preferences.GetProperty("compactionTriggerTokenCount").GetInt32());
      Assert.Equal(workingDirectory, preferences.GetProperty("workingDirectory").GetString());
    }

    string[] invalidBodies = [
      "{}",
      "{\"compactionTriggerTokenCount\":\"32768\"}",
      "{\"compactionTriggerTokenCount\":true}",
      "{\"compactionTriggerTokenCount\":0}",
      "{\"compactionTriggerTokenCount\":-1}",
      "{\"compactionTriggerTokenCount\":2147483648}",
      "{\"compactionTriggerTokenCount\":-2147483649}",
      "{\"compactionTriggerTokenCount\":1.5}",
      "[]"
    ];
    foreach (string invalidBody in invalidBodies) {
      using HttpResponseMessage invalid = await RequestAsync(server.Client, HttpMethod.Post,
          "/api/preferences/compaction-trigger", JsonBody(invalidBody));
      Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
      AssertPrivateResponse(invalid);

      using HttpResponseMessage current = await RequestAsync(server.Client, HttpMethod.Get, "/api/preferences");
      JsonElement preferences = await current.Content.ReadFromJsonAsync<JsonElement>();
      Assert.Equal(32768, preferences.GetProperty("compactionTriggerTokenCount").GetInt32());
    }

    using (HttpResponseMessage reset = await RequestAsync(server.Client, HttpMethod.Post,
        "/api/preferences/compaction-trigger", JsonBody("{\"compactionTriggerTokenCount\":null}"))) {
      Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
      AssertPrivateResponse(reset);
      JsonElement preferences = await reset.Content.ReadFromJsonAsync<JsonElement>();
      Assert.Equal(JsonValueKind.Null, preferences.GetProperty("compactionTriggerTokenCount").ValueKind);
      Assert.Equal(workingDirectory, preferences.GetProperty("workingDirectory").GetString());
    }
  }

}
