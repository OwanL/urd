using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Urd.Backend.Tests.Support;
using Urd.Core.Sessions;
using Urd.Server;
using Xunit;
using static Urd.Backend.Tests.Support.HttpTest;

namespace Urd.Backend.Tests.Hosting;

public sealed class PreferencesRoutesTests {
  [Fact]
  public async Task WorkingDirectoryPreferenceIsValidatedAndStampedOnNewSessionSnapshots() {
    using var database = new TemporaryDatabase();
    string workingDirectory = Path.Combine(Path.GetDirectoryName(database.Path)!, "preferred-directory");
    Directory.CreateDirectory(workingDirectory);
    await using var server = new RunningServer(new ServerOptions(Path.GetDirectoryName(database.Path)!, 0));
    await server.WaitForReadyAsync();

    using (HttpResponseMessage initial = await RequestAsync(server.Client, HttpMethod.Get, "/api/preferences")) {
      Assert.Equal(HttpStatusCode.OK, initial.StatusCode);
      AssertPrivateResponse(initial);
      Assert.Equal(Path.GetPathRoot(Environment.CurrentDirectory),
          (await initial.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("workingDirectory").GetString());
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

}
