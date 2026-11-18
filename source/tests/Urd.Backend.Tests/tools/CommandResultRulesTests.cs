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
  [InlineData("bash", "rg 'foo bar' . | head")]
  [InlineData("bash", "rg '\\bfoo\\b' .")]
  [InlineData("bash", "rg \"foo bar\" .")]
  [InlineData("grep", null)]
  public void GrepGroupsLosslessly(string tool, string? command) {
    const string input = "src/long-name.cs:01: first \r\nsrc/long-name.cs:2:\tsecond\r\nsrc/long-name.cs:2:\tsecond\r\nother/long-name.cs:3:\r\nsrc/long-name.cs:4:last\r\n";
    const string expected = "src/long-name.cs\r\n  01: first \r\n  2:\tsecond\r\n  2:\tsecond\r\nother/long-name.cs\r\n  3:\r\nsrc/long-name.cs\r\n  4:last\r\n";
    string grouped = CommandResultRules.GroupMatches(tool, Arguments(command), input);
    Assert.Equal(expected, grouped);
    Assert.Equal(input, ExpandGroups(grouped, "\r\n"));
  }

  [Theory]
  [InlineData("rg foo .; echo done")]
  [InlineData("echo foo | rg foo")]
  [InlineData("rg foo . | cat")]
  [InlineData("rg \"$pattern\" .")]
  [InlineData("rg 'unfinished")]
  public void GrepRejectsAmbiguousCommands(string command) => AssertGrepUnchanged("bash", command, "src/long-name.cs:1:a\nsrc/long-name.cs:2:b\n");

  [Theory]
  [InlineData("src/long-name.cs:1:a\nsrc/long-name.cs:2:b\nwarning\n")]
  [InlineData("src/long-name.cs:1:a\n\nsrc/long-name.cs:2:b\n")]
  [InlineData("src/long-name.cs:1:2:ambiguous\nsrc/long-name.cs:2:b\n")]
  [InlineData("src/long-name.cs:1:a:23:b\nsrc/long-name.cs:2:b\n")]
  [InlineData("  src/long-name.cs:1:a\n  src/long-name.cs:2:b\n")]
  [InlineData("src/long-name.cs:1:a\nsrc/long-name.cs\n  2:b\n")]
  [InlineData("src/long-name.cs:1:a\r\nsrc/long-name.cs:2:b\n")]
  [InlineData("a.c:1:x\na.c:2:y\n")]
  public void GrepRejectsCollisionsAndUnprofitableOutput(string input) => AssertGrepUnchanged("grep", null, input);

  [Fact]
  public void SummarizeDoesNotGroupGrep() {
    const string input = "src/long-name.cs:1:a\nsrc/long-name.cs:2:b\n";
    AssertUnchanged("bash", "rg foo .", input);
    AssertUnchanged("grep", null, input);
  }

  [Fact]
  public void GrepPreservesMissingTrailingNewline() {
    const string input = "src/long-name.cs:001:x\nsrc/long-name.cs:2:y";
    string grouped = CommandResultRules.GroupMatches("grep", Arguments(null), input);
    Assert.Equal("src/long-name.cs\n  001:x\n  2:y", grouped);
    Assert.Equal(input, ExpandGroups(grouped, "\n"));
  }

  private static string ExpandGroups(string text, string newline) {
    string? path = null;
    var lines = new List<string>();
    bool trailing = text.EndsWith(newline, StringComparison.Ordinal);
    string body = trailing ? text[..^newline.Length] : text;
    foreach (string line in body.Split(newline)) {
      if (line.StartsWith("  ", StringComparison.Ordinal)) lines.Add(path + ":" + line[2..]);
      else path = line;
    }
    return string.Join(newline, lines) + (trailing ? newline : "");
  }

  private static void AssertGrepUnchanged(string tool, string? command, string input) =>
    Assert.Equal(input, CommandResultRules.GroupMatches(tool, Arguments(command), input));

  private static JsonElement Arguments(string? command) => JsonSerializer.SerializeToElement(new { command });

  private static void AssertRewrite(string tool, string? command, string input, string expected, string rule) {
    var applied = new List<string>();
    Assert.Equal(expected, CommandResultRules.Summarize(tool, Arguments(command), input, applied));
    Assert.Equal(new[] { rule }, applied);
  }

  private static void AssertUnchanged(string tool, string? command, string input) {
    var applied = new List<string>();
    Assert.Equal(input, CommandResultRules.Summarize(tool, Arguments(command), input, applied));
    Assert.Empty(applied);
  }
}
