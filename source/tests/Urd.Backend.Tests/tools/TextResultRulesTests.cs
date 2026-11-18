using Urd.Core.Tools;
using Xunit;

namespace Urd.Backend.Tests.Tools;

public sealed class TextResultRulesTests {
  [Fact]
  public void NormalizeStripsAnsiAndNormalizesLineEndingsWithoutTrimmingContent() {
    Assert.Equal("first  \n \nsecond\rthird\n", TextResultRules.Normalize("\u001b[31mfirst  \u001b[0m\r\n \r\nsecond\rthird\r\n"));
  }

  [Theory]
  [InlineData("\n\nfirst\n\n\n\nsecond\n\n", "first\n\nsecond\n")]
  [InlineData("\n\n", "\n")]
  [InlineData("first\n\nsecond\n", "first\n\nsecond\n")]
  public void NormalizeCollapsesBlankRuns(string input, string expected) {
    Assert.Equal(expected, TextResultRules.Normalize(input));
  }

  [Fact]
  public void JsonMinificationPreservesLexemesDuplicateKeysAndEscapedStrings() {
    string input = " { \"n\": 1e+1000, \"n\": -0, \"s\": \"a \\\" b\\\\ c\" }\n";
    Assert.Equal("{\"n\":1e+1000,\"n\":-0,\"s\":\"a \\\" b\\\\ c\"}\n", TextResultRules.Normalize(input));
  }

  [Theory]
  [InlineData("{ invalid json }")]
  [InlineData("{\"x\": 1} trailing")]
  [InlineData("42")]
  public void InvalidOrScalarJsonIsNotMinified(string input) {
    Assert.Equal(input, TextResultRules.Normalize(input));
  }

  [Fact]
  public void CollapseDuplicatesPreservesLineAndExactRepeatCount() {
    string input = "start\n" + string.Concat(Enumerable.Repeat("  building module A  \n", 4)) + "done\n";
    Assert.Equal("start\n  building module A  \n  (... 3 identical lines)\ndone\n", TextResultRules.CollapseDuplicates(input));
  }

  [Fact]
  public void CollapseDuplicatesLeavesLiteralCountNotationAlone() {
    string input = string.Concat(Enumerable.Repeat("building module A\n", 3)) + "literal: (... 2 identical lines)\n";
    Assert.Equal(input, TextResultRules.CollapseDuplicates(input));
  }

  [Theory]
  [InlineData("⠋ fetching")]
  [InlineData("- fetching")]
  [InlineData("[50%] fetching")]
  [InlineData("[====> ] 50%")]
  [InlineData("WARNING: retry")]
  [InlineData("ERROR: failed")]
  public void CollapseDuplicatesPreservesProgressAndSeverity(string line) {
    string input = string.Concat(Enumerable.Repeat(line + "\n", 10));
    Assert.Equal(input, TextResultRules.CollapseDuplicates(input));
  }

  [Theory]
  [InlineData("x\nx\nx\n")]
  [InlineData("building module A\nbuilding module A\n")]
  [InlineData("\n\n\n")]
  public void CollapseDuplicatesRequiresThreeLinesAndShorterOutput(string input) {
    Assert.Equal(input, TextResultRules.CollapseDuplicates(input));
  }

  [Fact]
  public void CollapseDuplicatesPreservesLineEndingsAndFinalNewlineState() {
    string input = string.Concat(Enumerable.Repeat("building module A\r\n", 3)) + "done\r";
    Assert.Equal("building module A\r\n  (... 2 identical lines)\r\ndone\r", TextResultRules.CollapseDuplicates(input));
  }
}
