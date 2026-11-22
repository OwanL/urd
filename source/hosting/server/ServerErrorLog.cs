using System.Security;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Urd.Server;

internal sealed class ServerErrorLog {
  private static readonly JsonSerializerOptions json = new(JsonSerializerDefaults.Web) {
    Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
  };

  private readonly object gate = new();
  private readonly string directory;
  private readonly string logPath;
  private readonly string previousLogPath;
  private readonly long maximumBytes;

  internal ServerErrorLog(string dataRoot, long maximumBytes = 1024 * 1024) {
    ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
    ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);

    directory = dataRoot;
    logPath = Path.Combine(dataRoot, "errors.jsonl");
    previousLogPath = Path.Combine(dataRoot, "errors.previous.jsonl");
    this.maximumBytes = maximumBytes;
  }

  internal void Write<T>(string eventName, T details) {
    byte[] line;
    try {
      byte[] jsonBytes = JsonSerializer.SerializeToUtf8Bytes(
          new ErrorRecord<T>(DateTimeOffset.UtcNow, eventName, details), json);
      if (jsonBytes.LongLength >= maximumBytes)
        return;

      line = new byte[jsonBytes.Length + 1];
      jsonBytes.CopyTo(line, 0);
      line[^1] = (byte)'\n';
    } catch (Exception failure) when (failure is JsonException or NotSupportedException or InvalidOperationException) {
      return;
    }

    lock (gate) {
      try {
        Directory.CreateDirectory(directory);
        long currentLength = File.Exists(logPath) ? new FileInfo(logPath).Length : 0;
        if (currentLength > maximumBytes - line.Length)
          File.Move(logPath, previousLogPath, overwrite: true);

        using var stream = new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.Read);
        stream.Write(line);
      } catch (Exception failure) when (failure is IOException or UnauthorizedAccessException
          or SecurityException or ArgumentException or NotSupportedException) {
        // Error logging must not disrupt the server when local storage is unavailable.
      }
    }
  }

  private sealed record ErrorRecord<T>(DateTimeOffset Timestamp, string Event, T Details);
}
