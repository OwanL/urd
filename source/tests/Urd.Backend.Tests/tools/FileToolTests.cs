using System.Text;
using System.Text.Json;
using Urd.Core.Tools;
using Xunit;

namespace Urd.Backend.Tests.Tools;

public sealed class FileToolTests : IDisposable {
  private readonly string directory = Directory.CreateTempSubdirectory("urd-file-tools-").FullName;
  private readonly FileMutationQueue files = new();

  [Fact]
  public async Task FileFlowCreatesParentsAndEditsOriginalRegionsWithoutRewritingUntouchedBytes() {
    string path = "nested/flow.txt";
    const string original = "\uFEFFα\r\nbeta\nlast\r\n";
    var write = await Run(new WriteTool(files), new { path, content = original });
    Assert.False(write.IsError, write.Text);
    Assert.Contains(Encoding.UTF8.GetByteCount(original).ToString(System.Globalization.CultureInfo.InvariantCulture), write.Text, StringComparison.Ordinal);
    var edit = await Run(new EditTool(files), new {
      path,
      edits = new[] {
        new { oldText = "α\n", newText = "first\ninserted\n" },
        new { oldText = "beta", newText = "α" }
      }
    });
    Assert.False(edit.IsError, edit.Text);
    byte[] expected = Encoding.UTF8.GetBytes("\uFEFFfirst\r\ninserted\r\nα\nlast\r\n");
    Assert.Equal(expected, await File.ReadAllBytesAsync(Path.Combine(directory, path)));
    Assert.Equal(Encoding.UTF8.GetString(expected), (await Run(new ReadTool(files), new { path })).Text);
    var outside = await Run(new WriteTool(files), new { path = "../outside.txt", content = "allowed" }, workingDirectory: Path.Combine(directory, "nested"));
    Assert.False(outside.IsError, outside.Text);
    Assert.Equal("allowed", await File.ReadAllTextAsync(Path.Combine(directory, "outside.txt")));
  }

  [Fact]
  public async Task InvalidEditBatchesAndInvalidUtf8LeaveOriginalBytesUntouched() {
    string path = Path.Combine(directory, "invalid.txt");
    byte[] original = Encoding.UTF8.GetBytes("ababa\r\nunique\n");
    var batches = new[] {
      new[] { new { oldText = "aba", newText = "ambiguous overlapping occurrences" } },
      new[] { new { oldText = "ababa", newText = "one" }, new { oldText = "baba", newText = "overlap" } },
      new[] { new { oldText = "unique", newText = "changed" }, new { oldText = "changed", newText = "not in original" } },
      new[] { new { oldText = "", newText = "empty target" } }
    };
    foreach (var edits in batches) {
      await File.WriteAllBytesAsync(path, original);
      var result = await Run(new EditTool(files), new { path, edits });
      Assert.True(result.IsError, result.Text);
      Assert.Equal(original, await File.ReadAllBytesAsync(path));
    }
    byte[] invalid = [0x61, 0xC3, 0x28];
    await File.WriteAllBytesAsync(path, invalid);
    var rejected = await Run(new EditTool(files), new { path, edits = new[] { new { oldText = "a", newText = "b" } } });
    Assert.True(rejected.IsError);
    Assert.Contains("not valid UTF-8", rejected.Text, StringComparison.Ordinal);
    Assert.Equal(invalid, await File.ReadAllBytesAsync(path));
  }

  [Fact]
  public async Task ReadPagesRespectLineAndUtf8ByteLimitsAndExplainContinuation() {
    string path = Path.Combine(directory, "pages.txt");
    await File.WriteAllTextAsync(path, string.Join("\n", Enumerable.Range(1, 2003)));
    var tool = new ReadTool(files);
    var first = await Run(tool, new { path });
    Assert.False(first.IsError, first.Text);
    Assert.StartsWith("1\n2\n", first.Text, StringComparison.Ordinal);
    Assert.Contains("offset=2001", first.Text, StringComparison.Ordinal);
    Assert.Equal(2000, first.Details!.Value.GetProperty("outputLines").GetInt32());
    Assert.Equal("2001\n2002\n2003", (await Run(tool, new { path, offset = 2001 })).Text);
    Assert.Contains("offset=4", (await Run(tool, new { path, offset = 2, limit = 2 })).Text, StringComparison.Ordinal);
    Assert.True((await Run(tool, new { path, offset = 2004 })).IsError);
    Assert.True((await Run(tool, new { path, limit = 0 })).IsError);

    string unicodeLine = new('é', 1000);
    await File.WriteAllTextAsync(path, string.Join('\n', Enumerable.Repeat(unicodeLine, 30)));
    var bytes = await Run(tool, new { path });
    Assert.Contains("50KB limit", bytes.Text, StringComparison.Ordinal);
    int next = bytes.Details!.Value.GetProperty("nextOffset").GetInt32();
    string page = bytes.Text.Split("\n\n[", StringSplitOptions.None)[0];
    Assert.True(Encoding.UTF8.GetByteCount(page) <= 50 * 1024);
    Assert.Equal(25, page.Split('\n').Length);
    Assert.Equal(26, next);
    Assert.Equal(string.Join('\n', Enumerable.Repeat(unicodeLine, 5)), (await Run(tool, new { path, offset = next })).Text);
    await File.WriteAllTextAsync(path, new string('a', 50 * 1024 + 1));
    Assert.Contains("Use bash", (await Run(tool, new { path })).Text, StringComparison.Ordinal);
    await File.WriteAllTextAsync(path, "");
    Assert.Equal("", (await Run(tool, new { path })).Text);
    await File.WriteAllTextAsync(path, "one\r\ntwo\n");
    Assert.Equal("one\r\ntwo\n", (await Run(tool, new { path })).Text);
    Assert.Equal("", (await Run(tool, new { path, offset = 3 })).Text);
  }

  private Task<ToolOutput> Run(AgentTool tool, object arguments,
      bool acceptsImages = true, string? workingDirectory = null, CancellationToken cancellation = default) =>
    tool.RunAsync(JsonSerializer.SerializeToElement(arguments),
      new ToolContext(workingDirectory ?? directory, Guid.NewGuid(), "file-test", acceptsImages, _ => { }), cancellation);

  public void Dispose() => Directory.Delete(directory, recursive: true);
}
