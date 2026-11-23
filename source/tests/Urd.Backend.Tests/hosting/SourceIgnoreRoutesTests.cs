using System.Net;
using System.Text.Json;
using Urd.Backend.Tests.Support;
using Xunit;
using static Urd.Backend.Tests.Support.HttpTest;

namespace Urd.Backend.Tests.Hosting;

public sealed class SourceIgnoreRoutesTests {
  [Fact]
  public async Task SaveRereadAndPreviewFilterDiscoveredInstructions() {
    using var database = new TemporaryDatabase();
    var options = RunningServer.OptionsForIsolatedWorkspace(database);
    string directory = Path.Combine(Path.GetDirectoryName(database.Path)!, "workspace");
    string file = Path.Combine(directory, "CLAUDE.md");
    await File.WriteAllTextAsync(file, "Project instructions.");

    await using (var server = new RunningServer(options)) {
      await server.WaitForReadyAsync();
      JsonElement enabled = await PostAsync(server.Client, "/api/preferences/source-preview", new {
        category = "instructions", rules = new { paths = Array.Empty<string>(), filenames = Array.Empty<string>() }
      });
      Assert.Equal(file, Assert.Single(enabled.GetProperty("paths").EnumerateArray()).GetString());
      Assert.Equal(1, enabled.GetProperty("eligibleCount").GetInt32());
      Assert.True(enabled.GetProperty("estimatedTokens").GetInt32() > 0);
      Assert.Contains(enabled.GetProperty("filenames").EnumerateArray(), name => name.GetString() == "CLAUDE.md");
      JsonElement skillsPreview = await PostAsync(server.Client, "/api/preferences/source-preview", new {
        category = "skills", rules = new { paths = Array.Empty<string>(), filenames = Array.Empty<string>() }
      });
      Assert.Empty(skillsPreview.GetProperty("filenames").EnumerateArray());

      JsonElement ignored = await PostAsync(server.Client, "/api/preferences/source-preview", new {
        category = "instructions", rules = new { paths = new[] { file }, filenames = Array.Empty<string>() }
      });
      Assert.Equal(file, Assert.Single(ignored.GetProperty("paths").EnumerateArray()).GetString());
      Assert.Equal(0, ignored.GetProperty("eligibleCount").GetInt32());

      await PostAsync(server.Client, "/api/preferences/source-ignores", new {
        category = "instructions", paths = new[] { file }, filenames = Array.Empty<string>()
      });
      await AssertSavedAsync(server.Client, file);
    }

    await using var restarted = new RunningServer(options);
    await restarted.WaitForReadyAsync();
    await AssertSavedAsync(restarted.Client, file);
  }

  [Fact]
  public async Task IncludesAffectPreviewAndCategoryUpdatesPreserveOtherIncludes() {
    using var database = new TemporaryDatabase();
    string root = Path.Combine(Path.GetDirectoryName(database.Path)!, "workspace");
    string child = Path.Combine(root, "child");
    Directory.CreateDirectory(child);
    await File.WriteAllTextAsync(Path.Combine(child, "AGENTS.md"), "Instructions");
    await using var server = new RunningServer(RunningServer.OptionsForIsolatedWorkspace(database));
    await server.WaitForReadyAsync();
    JsonElement preview = await PostAsync(server.Client, "/api/preferences/source-preview", new {
      category = "instructions", rules = new { paths = new[] { root }, includes = new[] { child }, filenames = Array.Empty<string>() }
    });
    Assert.Equal(1, preview.GetProperty("eligibleCount").GetInt32());
    await PostAsync(server.Client, "/api/preferences/source-ignores", new {
      category = "skills", paths = new[] { root }, includes = new[] { child }, filenames = Array.Empty<string>()
    });
    JsonElement saved = await PostAsync(server.Client, "/api/preferences/source-ignores", new {
      category = "instructions", paths = new[] { root }, includes = new[] { child }, filenames = Array.Empty<string>()
    });
    JsonElement rules = saved.GetProperty("sourceIgnores");
    Assert.Equal(child, Assert.Single(rules.GetProperty("instructionIncludes").EnumerateArray()).GetString());
    Assert.Equal(child, Assert.Single(rules.GetProperty("skillIncludes").EnumerateArray()).GetString());
    using HttpResponseMessage invalid = await RequestAsync(server.Client, HttpMethod.Post, "/api/preferences/source-ignores",
        JsonBody("""{"category":"skills","paths":[],"includes":["relative"],"filenames":[]}"""));
    Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
  }

  [Fact]
  public async Task InvalidRequestsDoNotReplaceSavedRules() {
    using var database = new TemporaryDatabase();
    await using var server = new RunningServer(RunningServer.OptionsForIsolatedWorkspace(database));
    await server.WaitForReadyAsync();
    string file = Path.Combine(Path.GetDirectoryName(database.Path)!, "workspace", "AGENTS.md");
    await PostAsync(server.Client, "/api/preferences/source-ignores", new {
      category = "instructions", paths = new[] { file }, filenames = Array.Empty<string>()
    });

    foreach (object invalid in new object[] {
      new { category = "tools", paths = Array.Empty<string>(), filenames = Array.Empty<string>() },
      new { category = "instructions", paths = new[] { "relative" }, filenames = Array.Empty<string>() },
      new { category = "instructions", paths = Array.Empty<string>(), filenames = new[] { "notes/AGENTS.md" } },
      new { category = "instructions", paths = (string[]?)null, filenames = Array.Empty<string>() }
    }) {
      using HttpResponseMessage response = await RequestAsync(server.Client, HttpMethod.Post,
          "/api/preferences/source-ignores", JsonBody(JsonSerializer.Serialize(invalid)));
      Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
      await AssertSavedAsync(server.Client, file);
    }

    using HttpResponseMessage preview = await RequestAsync(server.Client, HttpMethod.Post,
        "/api/preferences/source-preview", JsonBody("""{"category":"tools","rules":{"paths":[],"filenames":[]}}"""));
    Assert.Equal(HttpStatusCode.BadRequest, preview.StatusCode);
    using HttpResponseMessage missing = await RequestAsync(server.Client, HttpMethod.Post,
        "/api/preferences/source-preview", JsonBody("""{"category":"instructions","rules":{"paths":[]}}"""));
    Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
  }

  [Fact]
  public async Task SkillUpdatePreservesInstructionRulesAndFilenames() {
    using var database = new TemporaryDatabase();
    await using var server = new RunningServer(RunningServer.OptionsForIsolatedWorkspace(database));
    await server.WaitForReadyAsync();
    string root = Path.Combine(Path.GetDirectoryName(database.Path)!, "workspace");
    string instruction = Path.Combine(root, "AGENTS.md");
    string skill = Path.Combine(root, "skill");
    await PostAsync(server.Client, "/api/preferences/source-ignores", new {
      category = "instructions", paths = new[] { instruction, instruction }, filenames = new[] { "claude.md" }
    });
    await PostAsync(server.Client, "/api/preferences/source-ignores", new {
      category = "skills", paths = new[] { skill }, filenames = Array.Empty<string>()
    });
    using HttpResponseMessage response = await RequestAsync(server.Client, HttpMethod.Get, "/api/preferences");
    using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    JsonElement rules = document.RootElement.GetProperty("sourceIgnores");
    Assert.Equal(instruction, Assert.Single(rules.GetProperty("instructions").EnumerateArray()).GetString());
    Assert.Equal(skill, Assert.Single(rules.GetProperty("skills").EnumerateArray()).GetString());
    Assert.Equal("CLAUDE.md", Assert.Single(rules.GetProperty("filenames").EnumerateArray()).GetString());
  }

  private static async Task AssertSavedAsync(HttpClient client, string file) {
    using HttpResponseMessage response = await RequestAsync(client, HttpMethod.Get, "/api/preferences");
    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    JsonElement rules = document.RootElement.GetProperty("sourceIgnores");
    Assert.Equal(file, Assert.Single(rules.GetProperty("instructions").EnumerateArray()).GetString());
    Assert.Empty(rules.GetProperty("filenames").EnumerateArray());
  }

  private static async Task<JsonElement> PostAsync(HttpClient client, string path, object body) {
    using HttpResponseMessage response = await RequestAsync(client, HttpMethod.Post, path,
        JsonBody(JsonSerializer.Serialize(body)));
    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    return document.RootElement.Clone();
  }
}
