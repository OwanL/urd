using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Urd.Core.Tools;
using Xunit;

namespace Urd.Backend.Tests.Tools;

public sealed class ToolResultPrunerTests : IDisposable {
  private readonly string directory = Directory.CreateTempSubdirectory("urd-result-pruner-").FullName;

  [Fact]
  public async Task SavesExactSourceAndReturnsMarkedReducedOutput() {
    string source = GitLogOutput(8);
    ToolOutput output = new(source, []);

    ToolOutput result = await PruneAsync("bash", output, "git log -8");

    string path = Assert.Single(Directory.GetFiles(directory));
    Assert.Equal(Encoding.UTF8.GetBytes(source), await File.ReadAllBytesAsync(path));
    string expected = string.Concat(Enumerable.Range(1, 8).Select(index =>
        $"{index.ToString("x40", CultureInfo.InvariantCulture)[..7]} subject {index} (HEAD -> main)\n"));
    Assert.Equal($"[Pruned: git-log. Read original: {path}]\n{expected}", result.Text);
    Assert.NotEqual(source, result.Text);
  }

  [Fact]
  public async Task KeepsOutputWhenPruningWouldNotSaveTheMinimumTokens() {
    const string source = "src/a.cs:1:x\nsrc/a.cs:2:y\n";
    var applied = new List<string>();
    Assert.Equal("src/a.cs\n  1: x\n  2: y\n", CommandResultRules.Apply("grep", Arguments(null), source, applied));
    Assert.Equal(new[] { "grep-group" }, applied);
    ToolOutput output = new(source, []);

    ToolOutput result = await PruneAsync("grep", output);

    Assert.Equal(source, result.Text);
    Assert.Empty(Directory.GetFiles(directory));
  }

  [Fact]
  public async Task KeepsOutputWhenOriginalCannotBeRecalled() {
    string source = GitLogOutput(8);
    ToolOutput output = new(source, []);

    ToolOutput result = await PruneAsync("bash", output, "git log -8", canRecall: false);

    Assert.Equal(TextResultRules.Normalize(source), result.Text);
    Assert.Empty(Directory.GetFiles(directory));
  }

  [Fact]
  public async Task KeepsOutputWhenStashingOriginalFails() {
    string source = GitLogOutput(8);
    ToolOutput output = new(source, []);
    string missingDirectory = Path.Combine(directory, "missing");

    ToolOutput result = await PruneAsync("bash", output, "git log -8", tempDirectory: missingDirectory);

    Assert.Equal(TextResultRules.Normalize(source), result.Text);
    Assert.False(Directory.Exists(missingDirectory));
    Assert.Empty(Directory.GetFiles(directory));
  }

  [Fact]
  public async Task ReadResultsBypassPruning() {
    ToolOutput output = new(RepeatedLines(), []);

    ToolOutput result = await PruneAsync("read", output);

    Assert.Same(output, result);
    Assert.Empty(Directory.GetFiles(directory));
  }

  [Fact]
  public async Task ErrorResultsBypassPruning() {
    ToolOutput output = new(RepeatedLines(), [], IsError: true);

    ToolOutput result = await PruneAsync("bash", output);

    Assert.Same(output, result);
    Assert.Empty(Directory.GetFiles(directory));
  }

  [Fact]
  public async Task ImageResultsBypassPruning() {
    ToolOutput output = new(RepeatedLines(), [new DataContent(new byte[] { 1, 2, 3 }, "image/png")]);

    ToolOutput result = await PruneAsync("bash", output);

    Assert.Same(output, result);
    Assert.Empty(Directory.GetFiles(directory));
  }

  [Theory]
  [InlineData("bash", "fullOutputPath")]
  [InlineData("web_read", "spillPath")]
  public async Task ResultsWithExistingSpillBypassPruning(string name, string spillKey) {
    ToolOutput output = new(RepeatedLines(), [],
        JsonSerializer.SerializeToElement(new Dictionary<string, string> { [spillKey] = "existing-spill.txt" }));

    ToolOutput result = await PruneAsync(name, output);

    Assert.Same(output, result);
    Assert.Empty(Directory.GetFiles(directory));
  }

  private Task<ToolOutput> PruneAsync(string name, ToolOutput output, string? command = null,
      bool canRecall = true, string? tempDirectory = null) =>
    ToolResultPruner.PruneAsync(name, Arguments(command), output, canRecall, tempDirectory ?? directory);

  private static JsonElement Arguments(string? command) => command is null
      ? JsonSerializer.SerializeToElement(new { })
      : JsonSerializer.SerializeToElement(new { command });

  private static string GitLogOutput(int count) => string.Concat(Enumerable.Range(1, count).Select(index =>
      $"commit {index.ToString("x40", CultureInfo.InvariantCulture)} (HEAD -> main)\r\n"
      + "Author: A very verbose author name <author@example.invalid>\r\n"
      + "Date:   Wed Nov 18 12:34:56 2026 +0000\r\n\r\n"
      + $"    subject {index}\r\n"
      + "    Detailed commit body retained only in the recall file.\r\n\r\n"));

  private static string RepeatedLines() => string.Concat(Enumerable.Repeat("same repeated status line\n", 24));

  public void Dispose() => Directory.Delete(directory, recursive: true);
}
