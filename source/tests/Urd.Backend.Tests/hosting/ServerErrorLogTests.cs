using System.Globalization;
using System.Text.Json;
using Urd.Server;
using Xunit;

namespace Urd.Backend.Tests.Hosting;

public sealed class ServerErrorLogTests {
  [Fact]
  public void WritesStructuredCamelCaseJsonLines() {
    using var dataRoot = new TemporaryDataRoot();
    var log = new ServerErrorLog(dataRoot.Path);

    log.Write("request-failed", new ErrorDetails(3, "connection lost"));

    string path = Path.Combine(dataRoot.Path, "errors.jsonl");
    using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
    JsonElement entry = document.RootElement;
    Assert.Equal("request-failed", entry.GetProperty("event").GetString());
    Assert.True(DateTimeOffset.TryParse(entry.GetProperty("timestamp").GetString(),
        CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset timestamp));
    Assert.Equal(TimeSpan.Zero, timestamp.Offset);
    JsonElement details = entry.GetProperty("details");
    Assert.Equal(3, details.GetProperty("retryCount").GetInt32());
    Assert.Equal("connection lost", details.GetProperty("failureMessage").GetString());
    Assert.False(details.TryGetProperty("RetryCount", out _));
  }

  [Fact]
  public void RotatesWithinTheByteLimitAndKeepsOnePreviousGeneration() {
    using var dataRoot = new TemporaryDataRoot();
    const int maximumBytes = 180;
    var log = new ServerErrorLog(dataRoot.Path, maximumBytes);

    log.Write("first", new { value = new string('x', 48) });
    log.Write("second", new { value = new string('x', 48) });
    log.Write("third", new { value = new string('x', 48) });

    string currentPath = Path.Combine(dataRoot.Path, "errors.jsonl");
    string previousPath = Path.Combine(dataRoot.Path, "errors.previous.jsonl");
    Assert.True(new FileInfo(currentPath).Length <= maximumBytes);
    Assert.True(new FileInfo(previousPath).Length <= maximumBytes);
    Assert.Equal("third", ReadEvent(currentPath));
    Assert.Equal("second", ReadEvent(previousPath));
  }

  [Fact]
  public void SkipsRecordsThatCannotFitInAnEmptyFile() {
    using var dataRoot = new TemporaryDataRoot();
    var log = new ServerErrorLog(dataRoot.Path, maximumBytes: 64);

    log.Write("oversized", new { value = new string('x', 100) });

    Assert.False(File.Exists(Path.Combine(dataRoot.Path, "errors.jsonl")));
  }

  [Fact]
  public void ConcurrentWritesRemainCompleteAndDistinct() {
    using var dataRoot = new TemporaryDataRoot();
    var log = new ServerErrorLog(dataRoot.Path, maximumBytes: 64 * 1024);

    Parallel.For(0, 64, index => log.Write("concurrent", new { index }));

    string[] lines = File.ReadAllLines(Path.Combine(dataRoot.Path, "errors.jsonl"));
    int[] indices = lines.Select(line => {
      using JsonDocument entry = JsonDocument.Parse(line);
      Assert.Equal("concurrent", entry.RootElement.GetProperty("event").GetString());
      return entry.RootElement.GetProperty("details").GetProperty("index").GetInt32();
    }).ToArray();
    Assert.Equal(64, indices.Length);
    Assert.Equal(Enumerable.Range(0, 64), indices.Order());
  }

  [Fact]
  public void UnavailableDataRootDoesNotThrow() {
    using var dataRoot = new TemporaryDataRoot();
    string blockedRoot = Path.Combine(dataRoot.Path, "not-a-directory");
    File.WriteAllText(blockedRoot, "block directory creation");
    var log = new ServerErrorLog(blockedRoot);

    log.Write("request-failed", new { reason = "unavailable" });

    Assert.Equal("block directory creation", File.ReadAllText(blockedRoot));
  }

  private static string? ReadEvent(string path) {
    using JsonDocument entry = JsonDocument.Parse(File.ReadAllText(path));
    return entry.RootElement.GetProperty("event").GetString();
  }

  private sealed record ErrorDetails(int RetryCount, string FailureMessage);

  private sealed class TemporaryDataRoot : IDisposable {
    internal string Path { get; } = Directory.CreateTempSubdirectory("urd-error-log-").FullName;

    public void Dispose() => Directory.Delete(Path, recursive: true);
  }
}
