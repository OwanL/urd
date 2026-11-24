using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Urd.Backend.Tests.Support;
using Urd.Core.Preferences;
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
  public async Task SavingWorkingDirectoryWarmsCatalogDuringIncompleteSetup() {
    using var database = new TemporaryDatabase();
    string directory = Path.Combine(Path.GetDirectoryName(database.Path)!, "warm-project");
    Directory.CreateDirectory(directory);
    await File.WriteAllTextAsync(Path.Combine(directory, "AGENTS.md"), "guidance");
    await using var server = new RunningServer(RunningServer.OptionsFor(database));
    await server.WaitForReadyAsync();
    string index = database.Path + ".instructions.sqlite";

    using (HttpResponseMessage invalid = await RequestAsync(server.Client, HttpMethod.Post,
        "/api/preferences/working-directory", JsonBody("{\"workingDirectory\":\"relative\"}")))
      Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    Assert.False(File.Exists(index));

    using (HttpResponseMessage saved = await RequestAsync(server.Client, HttpMethod.Post,
        "/api/preferences/working-directory", JsonBody(JsonSerializer.Serialize(new { workingDirectory = directory }))))
      Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    while (!File.Exists(index))
      await Task.Delay(25, timeout.Token);
  }

  [Fact]
  public async Task SubagentDepthRouteValidatesInputAndReturnsFullPreferences() {
    using var database = new TemporaryDatabase();
    await using var server = new RunningServer(RunningServer.OptionsFor(database));
    await server.WaitForReadyAsync();
    const string path = "/api/preferences/subagent-depth";
    using (HttpResponseMessage initial = await RequestAsync(server.Client, HttpMethod.Get, "/api/preferences")) {
      JsonElement preferences = await initial.Content.ReadFromJsonAsync<JsonElement>();
      Assert.Equal(4, preferences.GetProperty("maxSubagentDepth").GetInt32());
    }
    foreach (int depth in new[] { 0, int.MaxValue }) {
      using HttpResponseMessage saved = await RequestAsync(server.Client, HttpMethod.Post, path,
          JsonBody($"{{\"maxSubagentDepth\":{depth}}}"));
      Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
      AssertPrivateResponse(saved);
      JsonElement preferences = await saved.Content.ReadFromJsonAsync<JsonElement>();
      Assert.Equal(depth, preferences.GetProperty("maxSubagentDepth").GetInt32());
      Assert.True(preferences.TryGetProperty("workingDirectory", out _));
    }
    foreach (string body in new[] { "{}", "null", "[]", "{\"maxSubagentDepth\":null}",
        "{\"maxSubagentDepth\":\"1\"}", "{\"maxSubagentDepth\":true}",
        "{\"maxSubagentDepth\":-1}", "{\"maxSubagentDepth\":1.5}", "{\"maxSubagentDepth\":2147483648}" }) {
      using HttpResponseMessage invalid = await RequestAsync(server.Client, HttpMethod.Post, path, JsonBody(body));
      Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }
    using HttpResponseMessage current = await RequestAsync(server.Client, HttpMethod.Get, "/api/preferences");
    Assert.Equal(int.MaxValue, (await current.Content.ReadFromJsonAsync<JsonElement>())
        .GetProperty("maxSubagentDepth").GetInt32());
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

  [Fact]
  public async Task AuxiliaryModelAndProtectionPreferencesUseIndependentRoutes() {
    using var database = new TemporaryDatabase();
    await using var server = new RunningServer(RunningServer.OptionsForIsolatedWorkspace(database));
    await server.WaitForReadyAsync();
    Guid connectionId = Guid.NewGuid();
    string sourcePath = Path.Combine(Path.GetTempPath(), "repo", "AGENTS.md");

    using (HttpResponseMessage resources = await RequestAsync(server.Client, HttpMethod.Get, "/api/preferences/resources")) {
      Assert.Equal(HttpStatusCode.OK, resources.StatusCode);
      AssertPrivateResponse(resources);
      JsonElement catalog = await resources.Content.ReadFromJsonAsync<JsonElement>();
      string?[] tools = catalog.GetProperty("tools").EnumerateArray()
          .Select(resource => resource.GetProperty("id").GetString()).ToArray();
      Assert.Contains("read", tools);
      Assert.Contains("bash", tools);
      Assert.Contains("write", tools);
      Assert.Contains("edit", tools);
      Assert.Equal(JsonValueKind.Array, catalog.GetProperty("skills").ValueKind);
      Assert.Equal(JsonValueKind.Array, catalog.GetProperty("instructions").ValueKind);
    }

    using (HttpResponseMessage bucket = await RequestAsync(server.Client, HttpMethod.Post,
        "/api/preferences/model-bucket", JsonBody(JsonSerializer.Serialize(new {
          bucket = "small", models = new[] {
            new { connectionId, modelId = "small-model", reasoningEffort = (string?)"extraHigh" },
            new { connectionId, modelId = "another-small-model", reasoningEffort = (string?)null }
          }
        }))))
      Assert.Equal(HttpStatusCode.OK, bucket.StatusCode);

    using (HttpResponseMessage choice = await RequestAsync(server.Client, HttpMethod.Post,
        "/api/preferences/model-choice", JsonBody(JsonSerializer.Serialize(new {
          purpose = "skills", choice = new { bucket = (string?)null, connectionId, modelId = "skills-model" }
        }))))
      Assert.Equal(HttpStatusCode.OK, choice.StatusCode);

    using (HttpResponseMessage protection = await RequestAsync(server.Client, HttpMethod.Post,
        "/api/preferences/protected-resources", JsonBody(JsonSerializer.Serialize(new {
          category = "instructions", items = new[] { sourcePath }
        }))))
      Assert.Equal(HttpStatusCode.OK, protection.StatusCode);

    using (HttpResponseMessage defaults = await RequestAsync(server.Client, HttpMethod.Get, "/api/preferences")) {
      JsonElement preferences = await defaults.Content.ReadFromJsonAsync<JsonElement>();
      JsonElement models = preferences.GetProperty("modelChoices");
      Assert.Equal("small", models.GetProperty("tools").GetProperty("bucket").GetString());
      JsonElement[] small = models.GetProperty("small").EnumerateArray().ToArray();
      Assert.Equal(new[] { "small-model", "another-small-model" },
          small.Select(model => model.GetProperty("modelId").GetString()));
      Assert.All(small, model => Assert.Equal(connectionId, model.GetProperty("connectionId").GetGuid()));
      Assert.Equal("extraHigh", small[0].GetProperty("reasoningEffort").GetString());
      Assert.Equal(JsonValueKind.Null, small[1].GetProperty("reasoningEffort").ValueKind);
      Assert.Empty(models.GetProperty("medium").EnumerateArray());
      Assert.Equal(JsonValueKind.Null, models.GetProperty("skills").GetProperty("bucket").ValueKind);
      Assert.Equal("skills-model", models.GetProperty("skills").GetProperty("modelId").GetString());
      Assert.Equal<string?>(new ProtectedResourcePreferences().Tools,
          preferences.GetProperty("protectedResources").GetProperty("tools").EnumerateArray()
              .Select(item => item.GetString()).ToArray());
      Assert.Equal(sourcePath, Assert.Single(preferences.GetProperty("protectedResources")
          .GetProperty("instructions").EnumerateArray()).GetString());
    }

    using (HttpResponseMessage invalid = await RequestAsync(server.Client, HttpMethod.Post,
        "/api/preferences/model-bucket", JsonBody("""{"bucket":"small","models":null}""")))
      Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

    using (HttpResponseMessage cleared = await RequestAsync(server.Client, HttpMethod.Post,
        "/api/preferences/model-bucket", JsonBody("""{"bucket":"small","models":[]}"""))) {
      Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
      JsonElement preferences = await cleared.Content.ReadFromJsonAsync<JsonElement>();
      Assert.Empty(preferences.GetProperty("modelChoices").GetProperty("small").EnumerateArray());
    }

    using HttpResponseMessage removeToolDefaults = await RequestAsync(server.Client, HttpMethod.Post,
        "/api/preferences/protected-resources", JsonBody("{\"category\":\"tools\",\"items\":[]}"));
    Assert.Equal(HttpStatusCode.OK, removeToolDefaults.StatusCode);
    JsonElement saved = await removeToolDefaults.Content.ReadFromJsonAsync<JsonElement>();
    Assert.Equal(0, saved.GetProperty("protectedResources").GetProperty("tools").GetArrayLength());
  }

  [Fact]
  public async Task SubagentProviderRouteDistinguishesOmittedNullEmptyAndSelectedProviderIds() {
    using var database = new TemporaryDatabase();
    await using var server = new RunningServer(RunningServer.OptionsFor(database));
    await server.WaitForReadyAsync();
    Guid connectionId = Guid.NewGuid();
    const string path = "/api/preferences/subagent-providers";

    using (HttpResponseMessage selected = await RequestAsync(server.Client, HttpMethod.Post, path,
        JsonBody(JsonSerializer.Serialize(new { connectionIds = new[] { connectionId } })))) {
      Assert.Equal(HttpStatusCode.OK, selected.StatusCode);
      JsonElement preferences = await selected.Content.ReadFromJsonAsync<JsonElement>();
      Assert.Equal(connectionId, Assert.Single(
          preferences.GetProperty("subagentProviderConnectionIds").EnumerateArray()).GetGuid());
    }

    foreach (string invalidBody in new[] { "{}", """{"connectionIds":["00000000-0000-0000-0000-000000000000"]}""" }) {
      using HttpResponseMessage invalid = await RequestAsync(server.Client, HttpMethod.Post, path, JsonBody(invalidBody));
      Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    using (HttpResponseMessage current = await RequestAsync(server.Client, HttpMethod.Get, "/api/preferences")) {
      JsonElement preferences = await current.Content.ReadFromJsonAsync<JsonElement>();
      Assert.Equal(connectionId, Assert.Single(
          preferences.GetProperty("subagentProviderConnectionIds").EnumerateArray()).GetGuid());
    }

    using (HttpResponseMessage none = await RequestAsync(server.Client, HttpMethod.Post, path,
        JsonBody("""{"connectionIds":[]}"""))) {
      Assert.Equal(HttpStatusCode.OK, none.StatusCode);
      JsonElement preferences = await none.Content.ReadFromJsonAsync<JsonElement>();
      Assert.Empty(preferences.GetProperty("subagentProviderConnectionIds").EnumerateArray());
    }

    using HttpResponseMessage all = await RequestAsync(server.Client, HttpMethod.Post, path,
        JsonBody("""{"connectionIds":null}"""));
    Assert.Equal(HttpStatusCode.OK, all.StatusCode);
    JsonElement reset = await all.Content.ReadFromJsonAsync<JsonElement>();
    Assert.Equal(JsonValueKind.Null, reset.GetProperty("subagentProviderConnectionIds").ValueKind);
  }
}
