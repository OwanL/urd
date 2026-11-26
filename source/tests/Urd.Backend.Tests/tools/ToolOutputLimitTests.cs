using Urd.Core.Tools;
using Xunit;

namespace Urd.Backend.Tests.Tools;

public sealed class ToolOutputLimitTests {
  [Fact]
  public async Task SharedSpillPreventsPruning() {
    string text = string.Concat(Enumerable.Repeat("same repeated status line\n", 3000));
    ToolOutput result = await ToolOutputLimit.ApplyAsync(new ToolOutput(text, []));
    Assert.NotNull(result.Spill);
    try {
      Assert.Equal(text, await File.ReadAllTextAsync(result.Spill.Path));
      Assert.Same(result, ToolResultPruner.Prune("other", default, result));
      Assert.True(ToolOutputLimit.Fits(result.Text));
    } finally {
      File.Delete(result.Spill.Path);
    }
  }

  [Fact]
  public async Task ExplicitBoundariesPreserveSuffixAndIgnoreMarkerText() {
    string preview = string.Concat(Enumerable.Repeat("line\n", 3000)) + "\n\n[Showing fake marker";
    const string suffix = "\n\nCommand exited with code 2.";
    ToolOutput input = new(preview + "\n\n[discarded notice]" + suffix, [],
      Spill: new ToolOutputSpill("saved.txt", Tail: true, PreviewEnd: preview.Length, Suffix: suffix));
    ToolOutput result = await ToolOutputLimit.ApplyAsync(input);
    Assert.Contains("[Showing fake marker", result.Text, StringComparison.Ordinal);
    Assert.DoesNotContain("[discarded notice]", result.Text, StringComparison.Ordinal);
    Assert.EndsWith(suffix, result.Text, StringComparison.Ordinal);
    Assert.True(ToolOutputLimit.Fits(result.Text));
  }
}
