using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Urd.Backend.Tests.Support;
using Urd.Core.Sessions;
using Urd.Core.Storage;
using Urd.Server;
using Xunit;
using static Urd.Backend.Tests.Support.HttpTest;

namespace Urd.Backend.Tests.Hosting;

public sealed class SessionWorkingDirectoryRoutesTests {
  private static readonly JsonSerializerOptions json = new(JsonSerializerDefaults.Web) {
    Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
  };

  [Fact]
  public async Task ChangesDirectoryAndReturnsValidationMissingSessionAndAcceptedInputStatuses() {
    using var database = new TemporaryDatabase();
    string dataRoot = Path.GetDirectoryName(database.Path)!;
    string initialDirectory = Path.GetTempPath();
    string alternateDirectory = Path.Combine(dataRoot, "alternate");
    Directory.CreateDirectory(alternateDirectory);
    Guid sessionId;
    using (UrdStore store = UrdStore.Open(database.Path))
      sessionId = store.Sessions.CreatePrimary(Guid.NewGuid(), initialDirectory).Id;

    await using var server = new RunningServer(new ServerOptions(dataRoot, 0));
    await server.WaitForReadyAsync();
    string route = $"/api/sessions/{sessionId}/working-directory";
    using (HttpResponseMessage initial = await RequestAsync(server.Client, HttpMethod.Get,
        $"/api/sessions/{sessionId}/snapshot")) {
      Assert.Equal(HttpStatusCode.OK, initial.StatusCode);
      SessionSnapshot snapshot = (await initial.Content.ReadFromJsonAsync<SessionSnapshot>())!;
      Assert.True(snapshot.CanChangeWorkingDirectory);
      Assert.Equal(initialDirectory, snapshot.WorkingDirectory);
    }

    using (HttpResponseMessage missingField = await RequestAsync(server.Client, HttpMethod.Post, route, JsonBody("{}")))
      Assert.Equal(HttpStatusCode.BadRequest, missingField.StatusCode);
    using (HttpResponseMessage invalid = await RequestAsync(server.Client, HttpMethod.Post, route,
        JsonBody("{\"workingDirectory\":\"relative\"}")))
      Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    using (HttpResponseMessage missingDirectory = await RequestAsync(server.Client, HttpMethod.Post, route,
        JsonContent.Create(new { workingDirectory = Path.Combine(dataRoot, "missing") })))
      Assert.Equal(HttpStatusCode.BadRequest, missingDirectory.StatusCode);
    using (HttpResponseMessage unknown = await RequestAsync(server.Client, HttpMethod.Post,
        $"/api/sessions/{Guid.NewGuid()}/working-directory", JsonContent.Create(new { workingDirectory = alternateDirectory })))
      Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

    using (HttpResponseMessage changed = await RequestAsync(server.Client, HttpMethod.Post, route,
        JsonContent.Create(new { workingDirectory = alternateDirectory + Path.DirectorySeparatorChar }))) {
      Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
      using JsonDocument result = JsonDocument.Parse(await changed.Content.ReadAsStringAsync());
      Assert.Single(result.RootElement.EnumerateObject());
      Assert.Equal(alternateDirectory, result.RootElement.GetProperty("workingDirectory").GetString());
    }
    using HttpResponseMessage snapshotResponse = await RequestAsync(server.Client, HttpMethod.Get,
        $"/api/sessions/{sessionId}/snapshot");
    SessionSnapshot changedSnapshot = (await snapshotResponse.Content.ReadFromJsonAsync<SessionSnapshot>())!;
    Assert.Equal(alternateDirectory, changedSnapshot.WorkingDirectory);
    Assert.True(changedSnapshot.CanChangeWorkingDirectory);

    var submission = new UserMessageSubmission(Guid.NewGuid(), 0, "accepted", TestLlmSelection.Default);
    using (HttpResponseMessage accepted = await RequestAsync(server.Client, HttpMethod.Post,
        $"/api/sessions/{sessionId}/message-submissions", JsonContent.Create(submission, options: json)))
      Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
    using (HttpResponseMessage locked = await RequestAsync(server.Client, HttpMethod.Post, route,
        JsonContent.Create(new { workingDirectory = initialDirectory })))
      Assert.Equal(HttpStatusCode.Conflict, locked.StatusCode);
  }
}
