using System.Text.Json;
using Urd.Core.Providers;
using Urd.Core.Tools;
using Xunit;

namespace Urd.Backend.Tests.Tools;

public sealed class FileToolVariantTests : IDisposable {
  private readonly string directory = Directory.CreateTempSubdirectory("urd-file-variants-").FullName;
  private readonly FileMutationQueue files = new();

  [Theory]
  [InlineData(ModelFamily.Anthropic, "Read", "Write", "Edit")]
  [InlineData(ModelFamily.Google, "read_file", "write_file", "replace")]
  public void SelectsNamesAndSupportedSchemas(ModelFamily family, string read, string write, string edit) {
    Assert.Equal(read, FileToolVariants.For(new ReadTool(files), family).Name);
    Assert.Equal(write, FileToolVariants.For(new WriteTool(files), family).Name);
    AgentTool variant = FileToolVariants.For(new EditTool(files), family);
    Assert.Equal(edit, variant.Name);
    Assert.False(variant.ParametersSchema.GetProperty("properties").TryGetProperty("replace_all", out _));
    var generic = new ReadTool(files);
    Assert.Same(generic, FileToolVariants.For(generic, ModelFamily.OpenAI));
    Assert.Same(generic, FileToolVariants.For(generic, ModelFamily.Other));
  }

  [Theory]
  [InlineData(ModelFamily.Anthropic)]
  [InlineData(ModelFamily.Google)]
  public async Task WritesAndReplacesOnlyUniqueMatches(ModelFamily family) {
    AgentTool write = FileToolVariants.For(new WriteTool(files), family);
    AgentTool edit = FileToolVariants.For(new EditTool(files), family);
    Assert.False((await Run(write, new { file_path = "test.txt", content = "one\r\ntwo\r\none\r\n" })).IsError);
    var replacement = new Dictionary<string, object> {
      ["file_path"] = "test.txt", ["old_string"] = "one", ["new_string"] = "changed"
    };
    if (family == ModelFamily.Google)
      replacement["instruction"] = "Update the text";
    var ambiguous = await Run(edit, replacement);
    Assert.True(ambiguous.IsError);
    Assert.Equal("one\r\ntwo\r\none\r\n", await File.ReadAllTextAsync(Path.Combine(directory, "test.txt")));
    replacement["old_string"] = "two";
    var changed = await Run(edit, replacement);
    Assert.False(changed.IsError, changed.Text);
    Assert.Equal("one\r\nchanged\r\none\r\n", await File.ReadAllTextAsync(Path.Combine(directory, "test.txt")));
    replacement["old_string"] = "changed";
    replacement["new_string"] = "unexpected";
    replacement[family == ModelFamily.Google ? "allow_multiple" : "replace_all"] = true;
    var unsupported = await Run(edit, replacement);
    Assert.True(unsupported.IsError);
    Assert.Contains("Unsupported argument", unsupported.Text, StringComparison.Ordinal);
    Assert.Equal("one\r\nchanged\r\none\r\n", await File.ReadAllTextAsync(Path.Combine(directory, "test.txt")));
  }

  [Fact]
  public async Task GeminiRangeIsInclusiveStrictAndContinuationLeavesFileContentAlone() {
    await File.WriteAllTextAsync(Path.Combine(directory, "test.txt"), "Use offset=3 to continue.\ntwo\nthree\nfour");
    AgentTool read = FileToolVariants.For(new ReadTool(files), ModelFamily.Google);
    var page = await Run(read, new { file_path = "test.txt", start_line = 1, end_line = 2 });
    Assert.False(page.IsError, page.Text);
    Assert.Contains("Use offset=3 to continue.\ntwo", page.Text, StringComparison.Ordinal);
    Assert.Contains("Use start_line=3 to continue.", page.Text, StringComparison.Ordinal);
    Assert.StartsWith("three\nfour", (await Run(read, new { file_path = "test.txt", start_line = 3, end_line = 4 })).Text, StringComparison.Ordinal);
    Assert.True((await Run(read, new { file_path = "test.txt", start_line = 3, end_line = 2 })).IsError);
    Assert.True((await Run(read, new { file_path = "test.txt", start_line = 0 })).IsError);
    Assert.True((await Run(read, new { file_path = "test.txt", end_line = 0 })).IsError);
  }

  [Fact]
  public async Task ClaudeReadUsesOneBasedOffsetAndLineLimit() {
    await File.WriteAllTextAsync(Path.Combine(directory, "test.txt"), "first\nsecond\nthird");
    AgentTool read = FileToolVariants.For(new ReadTool(files), ModelFamily.Anthropic);
    var page = await Run(read, new { file_path = "test.txt", offset = 2, limit = 1 });
    Assert.False(page.IsError, page.Text);
    Assert.StartsWith("second", page.Text, StringComparison.Ordinal);
    Assert.DoesNotContain("third", page.Text, StringComparison.Ordinal);
    Assert.Contains("Use offset=3 to continue.", page.Text, StringComparison.Ordinal);
  }

  [Fact]
  public async Task RequiredArgumentsAndClaudeRangesAreValidated() {
    await File.WriteAllTextAsync(Path.Combine(directory, "x"), "a");
    AgentTool read = FileToolVariants.For(new ReadTool(files), ModelFamily.Anthropic);
    Assert.True((await Run(read, new { file_path = "x", offset = 0 })).IsError);
    Assert.True((await Run(read, new { file_path = "x", limit = 0 })).IsError);
    AgentTool edit = FileToolVariants.For(new EditTool(files), ModelFamily.Google);
    Assert.True((await Run(edit, new { file_path = "x", old_string = "a", new_string = "b" })).IsError);
  }

  private Task<ToolOutput> Run(AgentTool tool, object arguments) => tool.RunAsync(
      JsonSerializer.SerializeToElement(arguments), new ToolContext(directory, Guid.NewGuid(), "variant-test", true, _ => { }),
      CancellationToken.None);

  public void Dispose() => Directory.Delete(directory, recursive: true);
}
