using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Urd.Backend.Tests.Support;
using Urd.Core.Providers;
using Xunit;
using static Urd.Backend.Tests.Support.HttpTest;

namespace Urd.Backend.Tests.Hosting;

public sealed class SetupRoutesTests {
  [Fact]
  public async Task CompletionRequiresProviderEveryBucketAndExplicitExistingDirectoryAndPersistsAcrossRestart() {
    using var database = new TemporaryDatabase();
    string directory = Path.Combine(Path.GetDirectoryName(database.Path)!, "working-directory");
    Directory.CreateDirectory(directory);
    string completed;

    await using (var server = new RunningServer(RunningServer.OptionsFor(database))) {
      await server.WaitForReadyAsync();
      string initial = await StatusAsync(server.Client);
      AssertStatus(initial, true, false, false, false);
      await AssertIncompleteAsync(server.Client);

      using HttpResponseMessage provider = await RequestAsync(server.Client, HttpMethod.Post, "/api/providers/custom",
          JsonBody("""{"name":"Fixture","api":"openAIResponses","endpoint":"https://fixture.invalid/v1/","authentication":"none"}"""));
      Assert.Equal(HttpStatusCode.Created, provider.StatusCode);
      Guid connectionId = (await provider.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
      AssertStatus(await StatusAsync(server.Client), true, false, false, false);
      await AssertIncompleteAsync(server.Client);

      foreach (string bucket in new[] { "small", "medium", "large" }) {
        await PostOkAsync(server.Client, "/api/preferences/model-bucket", new {
          bucket, models = new[] { new { connectionId, modelId = $"{bucket}-model" } }
        });
        // Even all buckets do not implicitly confirm the default directory.
        await AssertIncompleteAsync(server.Client);
      }

      // Saved assignments alone are not usable until their provider has a matching catalog.
      AssertStatus(await StatusAsync(server.Client), true, false, false, false);
      var emptyCatalog = new ProviderModelCatalog(connectionId, DateTimeOffset.UtcNow, []);
      string emptyJson = JsonSerializer.Serialize(emptyCatalog).Replace("'", "''", StringComparison.Ordinal);
      database.Execute($"UPDATE provider_connections SET catalog_json = '{emptyJson}' WHERE connection_id = '{connectionId:D}';");
      AssertStatus(await StatusAsync(server.Client), true, false, false, false);
      var catalog = new ProviderModelCatalog(connectionId, DateTimeOffset.UtcNow,
          new[] { new ProviderModel("small-model", "Small"), new ProviderModel("medium-model", "Medium"),
              new ProviderModel("large-model", "Large") });
      string json = JsonSerializer.Serialize(catalog).Replace("'", "''", StringComparison.Ordinal);
      database.Execute($"UPDATE provider_connections SET catalog_json = '{json}' WHERE connection_id = '{connectionId:D}';");
      AssertStatus(await StatusAsync(server.Client), true, true, true, false);
      database.Execute($"UPDATE provider_connections SET authentication = {(int)AuthenticationMethod.ApiKey} WHERE connection_id = '{connectionId:D}';");
      AssertStatus(await StatusAsync(server.Client), true, false, false, false);
      await AssertIncompleteAsync(server.Client);
      database.Execute($"UPDATE provider_connections SET authentication = {(int)AuthenticationMethod.None} WHERE connection_id = '{connectionId:D}';");
      await PostOkAsync(server.Client, "/api/preferences/working-directory", new { workingDirectory = directory });
      AssertStatus(await StatusAsync(server.Client), true, true, true, true);
      foreach (var stale in new[] {
          new { connectionId, modelId = "missing-model" },
          new { connectionId = Guid.NewGuid(), modelId = "small-model" }
      }) {
        await PostOkAsync(server.Client, "/api/preferences/model-bucket", new { bucket = "small", models = new[] { stale } });
        AssertStatus(await StatusAsync(server.Client), true, true, false, true);
        await AssertIncompleteAsync(server.Client);
      }
      await PostOkAsync(server.Client, "/api/preferences/model-bucket", new {
        bucket = "small", models = new[] {
          new { connectionId = Guid.NewGuid(), modelId = "missing-model" },
          new { connectionId, modelId = "small-model" }
        }
      });
      AssertStatus(await StatusAsync(server.Client), true, true, true, true);
      database.Execute($"UPDATE provider_connections SET disconnecting = 1 WHERE connection_id = '{connectionId:D}';");
      AssertStatus(await StatusAsync(server.Client), true, false, false, true);
      await AssertIncompleteAsync(server.Client);
      database.Execute($"UPDATE provider_connections SET disconnecting = 0 WHERE connection_id = '{connectionId:D}';");
      foreach (string bucket in new[] { "small", "medium", "large" }) {
        await PostOkAsync(server.Client, "/api/preferences/model-bucket", new { bucket, models = Array.Empty<object>() });
        await AssertIncompleteAsync(server.Client);
        await PostOkAsync(server.Client, "/api/preferences/model-bucket", new {
          bucket, models = new[] { new { connectionId, modelId = $"{bucket}-model" } }
        });
      }
      Directory.Delete(directory);
      AssertStatus(await StatusAsync(server.Client), true, true, true, false);
      await AssertIncompleteAsync(server.Client);
      Directory.CreateDirectory(directory);

      completed = await CompleteAsync(server.Client);
      AssertStatus(completed, false, true, true, true);
      Assert.NotEqual(initial, completed);
      Assert.Equal(completed, await StatusAsync(server.Client));
      Assert.Equal(completed, await CompleteAsync(server.Client));
    }

    await using (var restarted = new RunningServer(RunningServer.OptionsFor(database))) {
      await restarted.WaitForReadyAsync();
      Assert.Equal(completed, await StatusAsync(restarted.Client));
      Assert.Equal(completed, await CompleteAsync(restarted.Client));
    }
  }

  private static void AssertStatus(string json, bool required, bool provider, bool buckets, bool directory) {
    using JsonDocument document = JsonDocument.Parse(json);
    JsonElement status = document.RootElement;
    Assert.Equal(4, status.EnumerateObject().Count());
    Assert.Equal(required, status.GetProperty("required").GetBoolean());
    Assert.Equal(provider, status.GetProperty("providerConfigured").GetBoolean());
    Assert.Equal(buckets, status.GetProperty("bucketsConfigured").GetBoolean());
    Assert.Equal(directory, status.GetProperty("workingDirectoryConfigured").GetBoolean());
  }

  private static async Task AssertIncompleteAsync(HttpClient client) {
    using HttpResponseMessage response = await RequestAsync(client, HttpMethod.Post, "/api/setup/complete", JsonBody("{}"));
    Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    AssertPrivateResponse(response);
  }

  private static async Task<string> StatusAsync(HttpClient client) {
    using HttpResponseMessage response = await RequestAsync(client, HttpMethod.Get, "/api/setup");
    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    AssertPrivateResponse(response);
    return await response.Content.ReadAsStringAsync();
  }

  private static async Task<string> CompleteAsync(HttpClient client) {
    using HttpResponseMessage response = await RequestAsync(client, HttpMethod.Post, "/api/setup/complete", JsonBody("{}"));
    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    AssertPrivateResponse(response);
    return await response.Content.ReadAsStringAsync();
  }

  private static async Task PostOkAsync(HttpClient client, string path, object body) {
    using HttpResponseMessage response = await RequestAsync(client, HttpMethod.Post, path,
        JsonBody(JsonSerializer.Serialize(body)));
    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
  }
}
