using System.Text.Json;

namespace Urd.Core.ToolCallDisplay;

internal static class WriteLabeler {
  internal static ToolCallLabel Create(JsonElement arguments, string? workingDirectory) {
    (string title, string? path) = FileToolLabeler.GetFile(arguments, "write", workingDirectory);
    return ToolCallLabels.CreateLabel(ToolCallIcon.Write, title, null, path);
  }
}
