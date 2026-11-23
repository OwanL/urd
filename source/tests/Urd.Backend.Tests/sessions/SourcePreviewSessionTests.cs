using System.Text.Json;
using Urd.Backend.Tests.Support;
using Urd.Core.Preferences;
using Urd.Core.Sessions;
using Urd.Core.Storage;
using Xunit;
using static Urd.Backend.Tests.Support.TestAsync;

namespace Urd.Backend.Tests.Sessions;

public sealed class SourcePreviewSessionTests {
  [Fact]
  public async Task PreviewUsesDraftRulesAndReturnsTheSourceCatalogWithoutPersistingThem() {
    using var database = new TemporaryDatabase();
    string directory = Path.Combine(Path.GetDirectoryName(database.Path)!, "project");
    Directory.CreateDirectory(directory);
    string file = Path.Combine(directory, "CLAUDE.md");
    await File.WriteAllTextAsync(file, "Project instructions.");
    using UrdStore store = UrdStore.Open(database.Path);
    var preferences = new PreferenceManager(store);
    preferences.SetWorkingDirectory(directory);
    await using var sessions = new SessionManager(store, _ => throw new InvalidOperationException("Preview does not use a model."),
        preferences: preferences);
    using var deadline = new CancellationTokenSource(TestTimeout);

    await Assert.ThrowsAsync<ArgumentException>(() => sessions.PreviewSourcesAsync("tools", new(), deadline.Token));
    SourcePreview baseline = await sessions.PreviewSourcesAsync("instructions", new(), deadline.Token);
    SourcePreview enabled = await sessions.PreviewSourcesAsync("instructions", new() { Filenames = [] }, deadline.Token);
    Assert.Equal([file], baseline.Paths);
    Assert.Equal(baseline.Paths, enabled.Paths);
    Assert.Equal(0, baseline.EligibleCount);
    Assert.Equal(1, enabled.EligibleCount);
    Assert.True(enabled.EstimatedTokens > 0);
    Assert.Contains("CLAUDE.md", preferences.Read().SourceIgnores.Filenames);

    using JsonDocument json = JsonDocument.Parse(JsonSerializer.Serialize(enabled));
    Assert.Equal(directory, json.RootElement.GetProperty("workingDirectory").GetString());
    Assert.Equal(file, Assert.Single(json.RootElement.GetProperty("paths").EnumerateArray()).GetString());
    Assert.Equal(1, json.RootElement.GetProperty("eligibleCount").GetInt32());
    Assert.Equal(enabled.EstimatedTokens, json.RootElement.GetProperty("estimatedTokens").GetInt32());
    Assert.Equal(enabled.Partial, json.RootElement.GetProperty("partial").GetBoolean());
  }
}
