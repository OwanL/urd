using System.Text.Json;

namespace Urd.Core.ToolCallDisplay;

internal static class ReadLabeler {
  internal static ToolCallLabel Create(JsonElement arguments, string? workingDirectory) {
    (string title, string? path) = FileToolLabeler.GetFile(arguments, "read", workingDirectory);
    return ToolCallLabels.CreateLabel(ToolCallIcon.Read, title, FileToolLabeler.ReadDetail(arguments), path);
  }
}
