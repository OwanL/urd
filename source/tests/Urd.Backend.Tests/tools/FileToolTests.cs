using System.Text;
using System.Text.Json;
using SkiaSharp;
using Urd.Core.Tools;
using Xunit;
using static Urd.Backend.Tests.Support.TestAsync;

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
  public async Task EmittedOrderIsAdmittedBeforeSuspensionAndQueuedCancellationHasNoEffects() {
    string path = Path.Combine(directory, "ordered.txt");
    var release = Signal();
    var held = files.ChangeAsync(path, async token => {
      await release.Task.WaitAsync(TestTimeout, token);
      return true;
    }, CancellationToken.None);
    using var cancellation = new CancellationTokenSource();
    var write = Run(new WriteTool(files), new { path, content = "first" });
    var cancelled = Run(new EditTool(files), new { path, edits = new[] { new { oldText = "first", newText = "wrong" } } }, cancellation: cancellation.Token);
    var edit = Run(new EditTool(files), new { path, edits = new[] { new { oldText = "first", newText = "last" } } });
    var read = Run(new ReadTool(files), new { path });
    await cancellation.CancelAsync();
    try {
      Assert.True((await cancelled.WaitAsync(TestTimeout)).IsError);
      Assert.False(write.IsCompleted);
      Assert.False(edit.IsCompleted);
      Assert.False(read.IsCompleted);
    } finally {
      release.TrySetResult();
    }
    await held.WaitAsync(TestTimeout);
    Assert.False((await write.WaitAsync(TestTimeout)).IsError);
    Assert.False((await edit.WaitAsync(TestTimeout)).IsError);
    Assert.Equal("last", (await read.WaitAsync(TestTimeout)).Text);
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

  [Fact]
  public async Task ReadProcessesImagesForBothModelKindsAndRejectsBinaryText() {
    string path = Path.Combine(directory, "image.data");
    using var bitmap = new SKBitmap(1, 1);
    bitmap.Erase(SKColors.Red);
    using var source = SKImage.FromBitmap(bitmap);
    using var encoded = source.Encode(SKEncodedImageFormat.Png, 100);
    byte[] png = encoded.ToArray();
    await File.WriteAllBytesAsync(path, png);
    foreach (bool acceptsImages in new[] { true, false }) {
      var result = await Run(new ReadTool(files), new { path }, acceptsImages: acceptsImages);
      Assert.False(result.IsError, result.Text);
      var image = Assert.Single(result.Images);
      Assert.Equal("image/png", image.MediaType);
      Assert.Equal(png, image.Data.ToArray());
      Assert.Equal(!acceptsImages, result.Text.Contains("omitted from the model request", StringComparison.Ordinal));
      Assert.False(result.Details!.Value.GetProperty("wasTransformed").GetBoolean());
    }
    await using (var oversized = new FileStream(path, FileMode.Open, FileAccess.Write))
      oversized.SetLength(64 * 1024 * 1024 + 1);
    var tooLarge = await Run(new ReadTool(files), new { path });
    Assert.True(tooLarge.IsError);
    Assert.Empty(tooLarge.Images);
    Assert.Contains("64MiB image processing limit", tooLarge.Text, StringComparison.Ordinal);
    byte[] bmpHeader = new byte[30];
    bmpHeader[0] = (byte)'B';
    bmpHeader[1] = (byte)'M';
    bmpHeader[14] = 40;
    bmpHeader[26] = 1;
    bmpHeader[28] = 24;
    await File.WriteAllBytesAsync(path, bmpHeader);
    var bmp = await Run(new ReadTool(files), new { path });
    Assert.True(bmp.IsError);
    Assert.Empty(bmp.Images);
    Assert.Contains("Image omitted", bmp.Text, StringComparison.Ordinal);
    await File.WriteAllBytesAsync(path, [0x61, 0, 0x62]);
    Assert.True((await Run(new ReadTool(files), new { path })).IsError);
    await File.WriteAllBytesAsync(path, [0xC3, 0x28]);
    Assert.Contains("not valid UTF-8", (await Run(new ReadTool(files), new { path })).Text, StringComparison.Ordinal);
  }

  private Task<ToolOutput> Run(AgentTool tool, object arguments,
      bool acceptsImages = true, string? workingDirectory = null, CancellationToken cancellation = default) =>
    tool.RunAsync(JsonSerializer.SerializeToElement(arguments),
      new ToolContext(workingDirectory ?? directory, Guid.NewGuid(), "file-test", acceptsImages, _ => { }), cancellation);

  public void Dispose() => Directory.Delete(directory, recursive: true);
}
