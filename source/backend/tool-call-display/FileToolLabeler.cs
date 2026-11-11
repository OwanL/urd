using System.Globalization;
using System.Text.Json;
using Urd.Core.Tools;

namespace Urd.Core.ToolCallDisplay;

internal static class FileToolLabeler {
  internal static (string Title, string? Path) GetFile(JsonElement arguments, string toolName, string? workingDirectory) {
    if (!ToolCallExcerpt.TryGetExactString(arguments, "path", out string path) || string.IsNullOrWhiteSpace(path))
      return (toolName, null);

    string title;
    try {
      title = Path.GetFileName(path);
    } catch (ArgumentException) {
      title = toolName;
    }
    if (string.IsNullOrEmpty(title))
      title = toolName;

    try {
      if (string.IsNullOrWhiteSpace(workingDirectory))
        return (title, null);
      return (title, ToolPaths.ResolveLexically(path, workingDirectory));
    } catch (Exception error) when (error is ArgumentException or IOException or NotSupportedException or System.Security.SecurityException) {
      return (title, null);
    }
  }

  internal static string? ReadDetail(JsonElement arguments) {
    bool hasOffset = ToolCallExcerpt.TryGetPositiveInteger(arguments, "offset", out int offset);
    bool hasLimit = ToolCallExcerpt.TryGetPositiveInteger(arguments, "limit", out int limit);
    if ((!hasOffset && !hasLimit) ||
        (ToolCallExcerpt.HasTopLevelOmitted(arguments) && (!hasOffset || !hasLimit)))
      return null;

    int first = hasOffset ? offset : 1;
    if (!hasLimit)
      return $"lines {first}+";
    long last = (long)first + limit - 1;
    return $"lines {first}–{last.ToString(CultureInfo.InvariantCulture)}";
  }

  internal static string? EditDetail(JsonElement arguments) {
    if (!ToolCallExcerpt.TryGetArrayCount(arguments, "edits", out long count))
      return null;
    string unit = count == 1 ? "edit" : "edits";
    return $"{count.ToString(CultureInfo.InvariantCulture)} {unit}";
  }

  internal static string? WriteDetail(JsonElement arguments) {
    if (!ToolCallExcerpt.TryGetExactString(arguments, "content", out string content))
      return null;
    int lines = CountLines(content);
    return $"{lines.ToString(CultureInfo.InvariantCulture)} lines";
  }

  private static int CountLines(string text) {
    if (text.Length == 0)
      return 0;

    int lines = 0;
    int index = 0;
    while (index < text.Length) {
      if (text[index] == '\r') {
        lines++;
        index += index + 1 < text.Length && text[index + 1] == '\n' ? 2 : 1;
      } else {
        if (text[index] == '\n')
          lines++;
        index++;
      }
    }
    if (text[^1] is not ('\r' or '\n'))
      lines++;
    return lines;
  }
}
