namespace Urd.Core.Tools;

// Failure kinds that tools settle as readable model-facing errors instead of crashing the call.
internal static class ToolFailures {
  public static bool IsExpected(Exception error) =>
      error is IOException or UnauthorizedAccessException or ArgumentException
          or InvalidOperationException or KeyNotFoundException or NotSupportedException;
}
