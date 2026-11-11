using System.Text.Json;
using Urd.Core.ToolCallDisplay;
using Xunit;

namespace Urd.Backend.Tests.ToolCallDisplay;

public sealed class ToolCallLabelsTests {
  [Theory]
  [InlineData("cd /repo && FOO=bar proto run moon :test --log", "moon", ":test --log")]
  [InlineData("cd /repo; FOO=bar env BAR=baz npx prettier --check app.cs", "prettier", "--check app.cs")]
  [InlineData("cd /repo", "cd", "/repo")]
  [InlineData("sudo time env TEST=1 proto run moon :test --log; echo done", "moon", ":test --log; echo done")]
  public void BashLabelSkipsDirectoryChangesAssignmentsAndWrappers(string command, string title, string detail) {
    ToolCallLabel label = ToolCallLabels.Create("bash", JsonSerializer.Serialize(new { command }), "/work");

    Assert.Equal(ToolCallIcon.Terminal, label.Icon);
    Assert.Equal(title, label.Title);
    Assert.Equal(detail, label.Detail);
    Assert.Null(label.Path);
  }

  [Fact]
  public void TruncatedBashUsesItsPrefixAndMarksTheDetail() {
    ToolCallLabel label = ToolCallLabels.Create("bash",
      "{\"command\":{\"$truncated\":\"proto run moon :test\",\"length\":4000}}", "/work");

    Assert.Equal("moon", label.Title);
    Assert.Equal(":test…", label.Detail);
  }

  [Fact]
  public void FileLabelsIncludeLexicalPathAndToolSpecificDetails() {
    string workingDirectory = Path.Combine(Path.GetTempPath(), "urd-label-work");
    ToolCallLabel read = ToolCallLabels.Create("read",
      JsonSerializer.Serialize(new { path = "src/app.cs", offset = 40, limit = 81 }), workingDirectory);
    ToolCallLabel write = ToolCallLabels.Create("write",
      JsonSerializer.Serialize(new { path = "src/new.cs", content = "one\ntwo\nthree" }), workingDirectory);
    ToolCallLabel edit = ToolCallLabels.Create("edit",
      "{\"path\":\"src/edit.cs\",\"edits\":[{},{},{\"$omitted\":3}]}", workingDirectory);

    Assert.Equal("app.cs", read.Title);
    Assert.Equal("lines 40–120", read.Detail);
    Assert.Equal(Path.GetFullPath(Path.Combine(workingDirectory, "src/app.cs")), read.Path);
    Assert.Equal("3 lines", write.Detail);
    Assert.Equal(Path.GetFullPath(Path.Combine(workingDirectory, "src/new.cs")), write.Path);
    Assert.Equal("5 edits", edit.Detail);
    Assert.Equal(Path.GetFullPath(Path.Combine(workingDirectory, "src/edit.cs")), edit.Path);
  }

  [Fact]
  public void PathsMayBeAbsoluteOrWindowsDriveSlashPathsWithoutFilesystemResolution() {
    string workingDirectory = Path.Combine(Path.GetTempPath(), "urd-label-work");
    string absolutePath = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "absolute.cs"));
    ToolCallLabel absolute = ToolCallLabels.Create("read",
      JsonSerializer.Serialize(new { path = absolutePath }), workingDirectory);
    ToolCallLabel driveSlash = ToolCallLabels.Create("read", "{\"path\":\"/c/Users/example/repo/file.cs\"}", workingDirectory);

    Assert.Equal(absolutePath, absolute.Path);
    Assert.Equal("absolute.cs", absolute.Title);
    if (OperatingSystem.IsWindows()) {
      Assert.Equal(Path.GetFullPath(@"c:\Users\example\repo\file.cs"), driveSlash.Path);
      Assert.Equal("file.cs", driveSlash.Title);
    } else {
      Assert.Equal(Path.GetFullPath("/c/Users/example/repo/file.cs"), driveSlash.Path);
    }
  }

  [Fact]
  public void OmittedReadArgumentsDoNotImplyDefaultRangeAndInvalidPathsAreSafe() {
    ToolCallLabel omitted = ToolCallLabels.Create("read", "{\"path\":\"file.txt\",\"limit\":2,\"$omitted\":1}", "/work");
    ToolCallLabel invalid = ToolCallLabels.Create("read", "{\"path\":\"\\u0000bad\"}", "/work");

    Assert.Null(omitted.Detail);
    Assert.Null(invalid.Path);
  }

  [Fact]
  public void TruncatedPathsFallBackToToolNameAndWriteCountsRequireExactContent() {
    ToolCallLabel path = ToolCallLabels.Create("edit",
      "{\"path\":{\"$truncated\":\"src/partial.cs\",\"length\":3000},\"edits\":[{}]}", "/work");
    ToolCallLabel write = ToolCallLabels.Create("write",
      "{\"path\":\"file.txt\",\"content\":{\"$truncated\":\"first line\",\"length\":3000}}", "/work");

    Assert.Equal("edit", path.Title);
    Assert.Null(path.Path);
    Assert.Equal("1 edit", path.Detail);
    Assert.Equal("file.txt", write.Title);
    Assert.Null(write.Detail);
    Assert.NotNull(write.Path);
  }

  [Fact]
  public void PendingCallLabelsUseShortTitlesAndCountKnownIds() {
    ToolCallLabel list = ToolCallLabels.Create("list_pending_calls", null, null);
    ToolCallLabel cancel = ToolCallLabels.Create("cancel_pending_calls",
      "{\"ids\":[\"a\",\"b\",{\"$omitted\":3}]}", null);

    Assert.Equal(ToolCallIcon.ListPending, list.Icon);
    Assert.Equal("pending calls", list.Title);
    Assert.Equal(ToolCallIcon.CancelPending, cancel.Icon);
    Assert.Equal("cancel 5 calls", cancel.Title);
    Assert.Null(cancel.Path);
  }

  [Fact]
  public void UnknownToolsRenderCompactArgumentsAndMarkers() {
    ToolCallLabel label = ToolCallLabels.Create("mcp__search",
      "{\"query\":\"hello\\nworld\",\"options\":{\"mode\":\"fast\"},\"cursor\":{\"$truncated\":\"secret\",\"length\":99},\"$omitted\":2}", null);

    Assert.Equal(ToolCallIcon.Generic, label.Icon);
    Assert.Equal("mcp__search", label.Title);
    Assert.Equal("query=hello world, options={mode=fast}, cursor=…, $omitted=…", label.Detail);
    Assert.Null(label.Path);
  }

  [Theory]
  [InlineData(null)]
  [InlineData("{malformed")]
  [InlineData("[]")]
  public void MissingOrMalformedExcerptsFallBackWithoutThrowing(string? excerpt) {
    ToolCallLabel label = ToolCallLabels.Create("read", excerpt, null);

    Assert.Equal(ToolCallIcon.Read, label.Icon);
    Assert.Equal("read", label.Title);
    Assert.Null(label.Detail);
    Assert.Null(label.Path);
  }

  [Fact]
  public void LabelsAreSingleLineAndEndTruncatedToTheirCaps() {
    ToolCallLabel label = ToolCallLabels.Create(new string('x', 140),
      JsonSerializer.Serialize(new { text = new string('y', 300) }), null);

    Assert.Equal(120, label.Title.Length);
    Assert.EndsWith("…", label.Title, StringComparison.Ordinal);
    Assert.Equal(240, label.Detail!.Length);
    Assert.EndsWith("…", label.Detail, StringComparison.Ordinal);
    Assert.DoesNotContain('\n', label.Title);
    Assert.DoesNotContain('\n', label.Detail);
  }
}
