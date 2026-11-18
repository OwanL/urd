using System.Text.Json;
using Urd.Core.Tools;
using Xunit;

namespace Urd.Backend.Tests.Tools;

public sealed class CommandResultRulesTests {
  [Fact]
  public void LongListingKeepsNamesAndLinks() {
    const string input = "total 2\ndrwxr-xr-x 2 u g 4096 Jul 6 12:34 dir\nlrwxrwxrwx 1 u g 5 Jul 6 2025 link -> target\n";
    AssertRewrite("bash", "ls -la", input, "dir/\nlink -> target\n", "ls-long");
  }

  [Theory]
  [InlineData("ls -l | head", "-rw-r--r-- 1 u g 3 Jul 6 12:34 file\n")]
  [InlineData("ls -- -l", "-rw-r--r-- 1 u g 3 Jul 6 12:34 file\n")]
  [InlineData("ls -l", "-rw-r--r-- 1 u g 3 Jul 6 12:34 file\nls: error\n")]
  public void ListingRequiresSingleCommandAndUnderstoodOutput(string command, string text) => AssertUnchanged("bash", command, text);

  [Fact]
  public void GitLogSummarizesCommitsAndRefs() {
    const string input = "commit abcdef123456789 (HEAD -> main)\nAuthor: A <a@b>\nDate:   today\n\n    Subject\n\n    Body\n";
    AssertRewrite("bash", "git log -5", input, "abcdef1 Subject (HEAD -> main)\n", "git-log");
  }

  [Theory]
  [InlineData("git log -p")]
  [InlineData("git log --stat")]
  [InlineData("git log --format=full")]
  [InlineData("git log | head")]
  public void GitLogPreservesExplicitOrAmbiguousDetail(string command) {
    const string input = "commit abcdef123456789\nAuthor: A\nDate:   today\n\n    Subject\n";
    AssertUnchanged("bash", command, input);
  }

  [Fact]
  public void GitLogDoesNotDiscardUnexpectedOutput() {
    const string input = "warning: trouble\ncommit abcdef123456789\nAuthor: A\nDate:   today\n\n    Subject\n";
    AssertUnchanged("bash", "git log", input);
  }

  [Theory]
  [InlineData("bash", "rg foo . | head")]
  [InlineData("grep", null)]
  public void GrepGroupsRepeatedPathsAndPreservesDiagnostics(string tool, string? command) {
    const string input = "src/long-name.cs:01:first\nsrc/long-name.cs:2:second\nwarning\n";
    AssertRewrite(tool, command, input, "src/long-name.cs\n  01: first\n  2: second\nwarning\n", "grep-group");
  }

  [Theory]
  [InlineData("rg foo .; echo done")]
  [InlineData("echo foo | rg foo")]
  [InlineData("rg foo . | cat")]
  public void GrepRejectsAmbiguousCommands(string command) => AssertUnchanged("bash", command, "src/long-name.cs:1:a\nsrc/long-name.cs:2:b\n");

  [Fact]
  public void GrepRequiresSavingsAndMajorityShape() {
    AssertUnchanged("grep", null, "a.cs:1:x\na.cs:2:y\n");
    AssertUnchanged("grep", null, "src/long-name.cs:1:x\nwarning\nerror\n");
  }

  private static JsonElement Arguments(string? command) => JsonSerializer.SerializeToElement(new { command });

  private static void AssertRewrite(string tool, string? command, string input, string expected, string rule) {
    var applied = new List<string>();
    Assert.Equal(expected, CommandResultRules.Apply(tool, Arguments(command), input, applied));
    Assert.Equal(new[] { rule }, applied);
  }

  private static void AssertUnchanged(string tool, string? command, string input) {
    var applied = new List<string>();
    Assert.Equal(input, CommandResultRules.Apply(tool, Arguments(command), input, applied));
    Assert.Empty(applied);
  }
}
