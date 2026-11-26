using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Urd.Core.Compaction;
using Urd.Core.Tools;
using Xunit;

namespace Urd.Backend.Tests.Tools;

public sealed class ToolResultPrunerTests {
  [Fact]
  public void KeepsUnprofitableGrepGroupingUnchanged() {
    const string source = "a.cs:1:x\nb.cs:2:y\n";
    Assert.Equal(source, CommandResultRules.GroupMatches("grep", Arguments(null), source));
    ToolOutput result = Prune("grep", new ToolOutput(source, []));
    Assert.Equal(source, result.Text);
  }

  [Theory]
  [InlineData("grep", null)]
  [InlineData("bash", "rg needle .")]
  public void GroupsGrepMatchesInline(string name, string? command) {
    const string source = "src/long-name.cs:1:first\nsrc/long-name.cs:2:second\n";
    const string expected = "src/long-name.cs\n  1:first\n  2:second\n";
    Assert.Equal(expected, CommandResultRules.GroupMatches(name, Arguments(command), source));
    ToolOutput result = Prune(name, new ToolOutput(source, []), command);
    Assert.Equal(expected, result.Text);
  }

  [Fact]
  public void CollapsesDuplicatesInline() {
    ToolOutput result = Prune("bash", new ToolOutput(RepeatedLines(), []));
    Assert.Equal("same repeated status line\n  (... 23 identical lines)\n", result.Text);
  }

  [Fact]
  public void KeepsDistinctProgressLines() {
    const string source = "[=====     ] 50%\n[======    ] 60%\n[=======   ] 70%\n";
    ToolOutput result = Prune("bash", new ToolOutput(source, []));
    Assert.Equal(source, result.Text);
  }

  [Theory]
  [InlineData("ls -l", "-rw-r--r-- 1 u g 3 Jul 6 12:34 file\n")]
  [InlineData("git log", "commit abcdef123456789\nAuthor: A\nDate:   today\n\n    Subject\n")]
  public void SmallCommandDetailsStayInline(string command, string source) {
    ToolOutput result = Prune("bash", new ToolOutput(source, []), command);
    Assert.Equal(TextResultRules.Normalize(source), result.Text);
  }

  [Fact]
  public void DetailedLargeLsAndGitOutputsStayInline() {
    string lsOutput = string.Concat(Enumerable.Range(1, 32).Select(index =>
        $"-rw-r--r-- 1 detailed-owner-{index:D2} development-group-{index:D2} 4096 Jul 6 12:34 src/detailed-file-{index:D2}.cs\n"));
    string gitOutput = DetailedGitLogOutput(24);

    Assert.Equal(TextResultRules.Normalize(lsOutput), Prune("bash", new ToolOutput(lsOutput, []), "ls -l").Text);
    Assert.Equal(TextResultRules.Normalize(gitOutput), Prune("bash", new ToolOutput(gitOutput, []), "git log").Text);
  }

  [Fact]
  public void ReadResultsBypassPruning() {
    ToolOutput output = new(RepeatedLines(), []);
    Assert.Same(output, Prune("read", output));
  }

  [Fact]
  public void ErrorResultsBypassPruning() {
    ToolOutput output = new(RepeatedLines(), [], IsError: true);
    Assert.Same(output, Prune("bash", output));
  }

  [Fact]
  public void ImageResultsBypassPruning() {
    ToolOutput output = new(RepeatedLines(), [new DataContent(new byte[] { 1, 2, 3 }, "image/png")]);
    Assert.Same(output, Prune("bash", output));
  }

  [Theory]
  [InlineData("bash")]
  [InlineData("web_read")]
  [InlineData("another_tool")]
  public void ResultsWithExistingSpillBypassPruning(string name) {
    ToolOutput output = new(RepeatedLines(), [], Spill: new ToolOutputSpill("existing-spill.txt"));
    Assert.Same(output, Prune(name, output));
  }

  [Fact]
  public void SpillLikeDetailsDoNotSuppressPruning() {
    ToolOutput output = new(RepeatedLines(), [], JsonSerializer.SerializeToElement(new { fullOutputPath = "not-a-spill" }));
    Assert.NotEqual(output.Text, Prune("bash", output).Text);
  }

  private static ToolOutput Prune(string name, ToolOutput output, string? command = null) =>
    ToolResultPruner.Prune(name, Arguments(command), output);

  private static JsonElement Arguments(string? command) => command is null
      ? JsonSerializer.SerializeToElement(new { })
      : JsonSerializer.SerializeToElement(new { command });

  private static string DetailedGitLogOutput(int count) => string.Concat(Enumerable.Range(1, count).Select(index =>
      $"commit {index.ToString("x40", CultureInfo.InvariantCulture)} (HEAD -> main)\r\n"
      + "Author: A very verbose author name <author@example.invalid>\r\n"
      + "Date:   Wed Nov 18 12:34:56 2026 +0000\r\n\r\n"
      + $"    subject {index}\r\n"
      + $"    Changed component {index}; preserve this commit body along with its author and date.\r\n"
      + "\r\n"));

  private static string RepeatedLines() => string.Concat(Enumerable.Repeat("same repeated status line\n", 24));
}
