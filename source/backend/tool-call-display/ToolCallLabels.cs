using System.Text.Json;

namespace Urd.Core.ToolCallDisplay;

public static class ToolCallLabels {
  public static ToolCallLabel Create(string? toolName, string? argumentsExcerpt, string? workingDirectory) {
    string fallbackTitle = string.IsNullOrWhiteSpace(toolName) ? "tool" : toolName;
    if (toolName == "list_pending_calls")
      return CreateLabel(ToolCallIcon.ListPending, "pending calls", null, null);
    if (!ToolCallExcerpt.TryParse(argumentsExcerpt, out JsonDocument? document))
      return Fallback(toolName, fallbackTitle);

    using (document) {
      JsonElement arguments = document!.RootElement;
      if (toolName is "read" or "write" or "edit") {
        (string title, string? path) = FileToolLabeler.GetFileLabel(arguments, toolName, workingDirectory);
        if (toolName == "read" && title.Equals("SKILL.md", StringComparison.OrdinalIgnoreCase) &&
            Path.GetFileName(Path.GetDirectoryName(path)) is { Length: > 0 } skillDirectory)
          return CreateLabel(ToolCallIcon.Skill, skillDirectory, FileToolLabeler.ReadDetail(arguments), path);

        ToolCallIcon icon = toolName switch {
          "read" => ToolCallIcon.Read,
          "write" => ToolCallIcon.Write,
          _ => ToolCallIcon.Edit
        };
        string? detail = toolName switch {
          "read" => FileToolLabeler.ReadDetail(arguments),
          "edit" => FileToolLabeler.EditDetail(arguments),
          _ => null
        };
        return CreateLabel(icon, title, detail, path);
      }
      return toolName switch {
        "bash" => BashLabeler.Create(arguments),
        "cancel_pending_calls" => CancelPendingLabeler.Create(arguments),
        "ask_agent" => AgentQuestionLabeler.CreateAsk(arguments),
        "answer_agent" => AgentQuestionLabeler.CreateAnswer(arguments),
        _ => CreateLabel(ToolCallIcon.Generic, fallbackTitle, ToolCallExcerpt.FormatCompactArguments(arguments), null)
      };
    }
  }

  private static ToolCallLabel Fallback(string? toolName, string fallbackTitle) => toolName switch {
    "bash" => CreateLabel(ToolCallIcon.Terminal, fallbackTitle, null, null),
    "read" => CreateLabel(ToolCallIcon.Read, fallbackTitle, null, null),
    "write" => CreateLabel(ToolCallIcon.Write, fallbackTitle, null, null),
    "edit" => CreateLabel(ToolCallIcon.Edit, fallbackTitle, null, null),
    "cancel_pending_calls" => CancelPendingLabeler.Fallback(),
    "ask_agent" => AgentQuestionLabeler.FallbackAsk(),
    "answer_agent" => AgentQuestionLabeler.FallbackAnswer(),
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
