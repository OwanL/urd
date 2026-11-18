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
  public void ReduceAppliesProgressBeforeDuplicateCollapseAndReportsOnlyChanges() {
    string input = "⠋ fetching\n" + string.Concat(Enumerable.Repeat("building module A\n", 3)) + "done\n";
    var applied = new List<string>();
    Assert.Equal("building module A\n  (... 2 identical lines)\ndone\n", TextResultRules.Reduce(input, applied));
    Assert.Equal(new[] { "progress-noise", "duplicate-collapse" }, applied);
  }

  [Fact]
  public void ReducePreservesSeverityLinesAndNonProgressPercentages() {
    string input = "⠋ fetching\nWARNING: ⠋ retry\nCoverage: 85%\nDone\n";
    var applied = new List<string>();
    Assert.Equal("WARNING: ⠋ retry\nCoverage: 85%\nDone\n", TextResultRules.Reduce(input, applied));
    Assert.Equal(new[] { "progress-noise" }, applied);
  }

  [Theory]
  [InlineData("⠋\n⠙\n")]
  [InlineData("x\nx\nx\n")]
  [InlineData("WARNING: retry\nWARNING: retry\nWARNING: retry\n")]
  [InlineData("building module A\nbuilding module A\n")]
  public void ReduceKeepsNoiseOnlyShortOrSeverityRuns(string input) {
    var applied = new List<string>();
    Assert.Equal(input, TextResultRules.Reduce(input, applied));
    Assert.Empty(applied);
  }

  [Fact]
  public void ReduceHandlesCrLfAndPreservesBareCarriageReturn() {
    var applied = new List<string>();
    Assert.Equal("done\r", TextResultRules.Reduce("⠋ fetching\r\ndone\r", applied));
    Assert.Equal(new[] { "progress-noise" }, applied);
  }
}
