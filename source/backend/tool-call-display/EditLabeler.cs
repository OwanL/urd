using System.Text.Json;

namespace Urd.Core.ToolCallDisplay;

internal static class EditLabeler {
  internal static ToolCallLabel Create(JsonElement arguments, string? workingDirectory) {
    (string title, string? path) = FileToolLabeler.GetFile(arguments, "edit", workingDirectory);
    return ToolCallLabels.CreateLabel(ToolCallIcon.Edit, title, FileToolLabeler.EditDetail(arguments), path);
  }
}
