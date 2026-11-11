using System.Text.Json;

namespace Urd.Core.ToolCallDisplay;

public static class ToolCallLabels {
  public static ToolCallLabel Create(string? toolName, string? argumentsExcerpt, string? workingDirectory) {
    string fallbackTitle = string.IsNullOrWhiteSpace(toolName) ? "tool" : toolName;
    if (toolName == "list_pending_calls")
      return ListPendingLabeler.Create();
    if (!ToolCallExcerpt.TryParse(argumentsExcerpt, out JsonDocument? document))
      return Fallback(toolName, fallbackTitle);

    using (document) {
      JsonElement arguments = document!.RootElement;
      return toolName switch {
        "bash" => BashLabeler.Create(arguments),
        "read" => ReadLabeler.Create(arguments, workingDirectory),
        "write" => WriteLabeler.Create(arguments, workingDirectory),
        "edit" => EditLabeler.Create(arguments, workingDirectory),
        "cancel_pending_calls" => CancelPendingLabeler.Create(arguments),
        _ => GenericToolLabeler.Create(fallbackTitle, arguments)
      };
    }
  }

  private static ToolCallLabel Fallback(string? toolName, string fallbackTitle) => toolName switch {
    "bash" => CreateLabel(ToolCallIcon.Terminal, fallbackTitle, null, null),
    "read" => CreateLabel(ToolCallIcon.Read, fallbackTitle, null, null),
    "write" => CreateLabel(ToolCallIcon.Write, fallbackTitle, null, null),
    "edit" => CreateLabel(ToolCallIcon.Edit, fallbackTitle, null, null),
    "cancel_pending_calls" => CancelPendingLabeler.Fallback(),
    _ => CreateLabel(ToolCallIcon.Generic, fallbackTitle, null, null)
  };

  internal static ToolCallLabel CreateLabel(ToolCallIcon icon, string title, string? detail, string? path) {
    string safeTitle = ToolCallLabelText.OneLine(title, 120);
    string? safeDetail = string.IsNullOrWhiteSpace(detail) ? null : ToolCallLabelText.OneLine(detail, 240);
    if (string.Equals(safeTitle, safeDetail, StringComparison.Ordinal))
      safeDetail = null;
    return new ToolCallLabel(icon, safeTitle, safeDetail, path);
  }
}
