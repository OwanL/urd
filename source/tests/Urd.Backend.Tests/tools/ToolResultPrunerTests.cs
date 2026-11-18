using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Urd.Core.Compaction;
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
  public async Task KeepsUnprofitableGrepGroupingUnchanged() {
    const string source = "a.cs:1:x\nb.cs:2:y\n";
    Assert.Equal(source, CommandResultRules.GroupMatches("grep", Arguments(null), source));
    ToolOutput output = new(source, []);

    ToolOutput result = await PruneAsync("grep", output);

    Assert.Equal(source, result.Text);
    Assert.Empty(Directory.GetFiles(directory));
  }

  [Theory]
  [InlineData("grep", null)]
  [InlineData("bash", "rg needle .")]
  public async Task GroupsGrepMatchesInline(string name, string? command) {
    const string source = "src/long-name.cs:1:first\nsrc/long-name.cs:2:second\n";
    const string expected = "src/long-name.cs\n  1:first\n  2:second\n";
    Assert.Equal(expected, CommandResultRules.GroupMatches(name, Arguments(command), source));
    ToolOutput result = await PruneAsync(name, new ToolOutput(source, []), command);
    Assert.Equal(expected, result.Text);
    Assert.Empty(Directory.GetFiles(directory));
  }

  [Fact]
  public async Task CollapsesDuplicatesInlineWithoutRecall() {
    ToolOutput result = await PruneAsync("bash", new ToolOutput(RepeatedLines(), []), canRecall: false);
    Assert.Equal("same repeated status line\n  (... 23 identical lines)\n", result.Text);
    Assert.DoesNotContain("Read original", result.Text);
    Assert.Empty(Directory.GetFiles(directory));
  }

  [Fact]
  public async Task KeepsDistinctProgressLines() {
    const string source = "[=====     ] 50%\n[======    ] 60%\n[=======   ] 70%\n";
    ToolOutput result = await PruneAsync("bash", new ToolOutput(source, []));
    Assert.Equal(source, result.Text);
    Assert.Empty(Directory.GetFiles(directory));
  }

  [Theory]
  [InlineData("ls -l", "-rw-r--r-- 1 u g 3 Jul 6 12:34 file\n")]
  [InlineData("git log", "commit abcdef123456789\nAuthor: A\nDate:   today\n\n    Subject\n")]
  public async Task SmallCommandDetailsStayInline(string command, string source) {
    ToolOutput result = await PruneAsync("bash", new ToolOutput(source, []), command);
    Assert.Equal(TextResultRules.Normalize(source), result.Text);
    Assert.DoesNotContain("Read original", result.Text);
    Assert.Empty(Directory.GetFiles(directory));
  }

  [Theory]
  [InlineData("ls -l", "ls-long", 255)]
  [InlineData("ls -l", "ls-long", 256)]
  [InlineData("git log", "git-log", 255)]
  [InlineData("git log", "git-log", 256)]
  public async Task CommandSummariesStashOnlyAtNetThreshold(string command, string rule, int saving) {
    string placeholderPath = Path.Combine(directory, $"urd-result-{new string('0', 32)}.txt");
    string source = "", reduced = "";
    for (int padding = 0; padding < 4096; padding++) {
      source = command == "ls -l"
          ? $"-rw-r--r-- 1 {new string('u', padding + 1)} g 3 Jul 6 12:34 file\r\n"
          : $"commit abcdef123456789\r\nAuthor: A\r\nDate:   today\r\n\r\n    Subject\r\n    {new string('x', padding)}\r\n";
      reduced = CommandResultRules.Summarize("bash", Arguments(command), TextResultRules.Normalize(source), []);
      string marked = $"[Pruned: {rule}. Read original: {placeholderPath}]\n{reduced}";
      if (ContextTokenBudget.EstimateText(TextResultRules.Normalize(source)) - ContextTokenBudget.EstimateText(marked) == saving)
        break;
    }
    string candidate = $"[Pruned: {rule}. Read original: {placeholderPath}]\n{reduced}";
    Assert.Equal(saving, ContextTokenBudget.EstimateText(TextResultRules.Normalize(source)) - ContextTokenBudget.EstimateText(candidate));
    ToolOutput result = await PruneAsync("bash", new ToolOutput(source, []), command);
    if (saving == 255) {
      Assert.Equal(TextResultRules.Normalize(source), result.Text);
      Assert.Empty(Directory.GetFiles(directory));
    } else {
      string path = Assert.Single(Directory.GetFiles(directory));
      Assert.Equal($"[Pruned: {rule}. Read original: {path}]\n{reduced}", result.Text);
      Assert.Equal(Encoding.UTF8.GetBytes(source), await File.ReadAllBytesAsync(path));
    }
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
