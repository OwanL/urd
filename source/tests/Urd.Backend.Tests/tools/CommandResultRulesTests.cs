using System.Text.Json;
using Urd.Core.Tools;
using Xunit;

namespace Urd.Backend.Tests.Tools;

public sealed class CommandResultRulesTests {
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

}
