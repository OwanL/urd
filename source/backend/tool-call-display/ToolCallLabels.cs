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
      if (toolName is not null && FileIcon(toolName) is { } icon) {
        (string title, string? path) = FileToolLabeler.GetFileLabel(arguments, toolName, workingDirectory);
        if (icon == ToolCallIcon.Read && title.Equals("SKILL.md", StringComparison.OrdinalIgnoreCase) &&
            Path.GetFileName(Path.GetDirectoryName(path)) is { Length: > 0 } skillDirectory)
          return CreateLabel(ToolCallIcon.Skill, skillDirectory, null, null);

        string? detail = toolName switch {
          "read" or "Read" => FileToolLabeler.ReadDetail(arguments),
          "read_file" => FileToolLabeler.GeminiReadDetail(arguments),
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

  private static ToolCallIcon? FileIcon(string? toolName) => toolName switch {
    "read" or "Read" or "read_file" => ToolCallIcon.Read,
    "write" or "Write" or "write_file" => ToolCallIcon.Write,
    "edit" or "Edit" or "replace" => ToolCallIcon.Edit,
    _ => null
  };

  private static ToolCallLabel Fallback(string? toolName, string fallbackTitle) => toolName switch {
    "bash" => CreateLabel(ToolCallIcon.Terminal, fallbackTitle, null, null),
    "cancel_pending_calls" => CancelPendingLabeler.Fallback(),
    "ask_agent" => AgentQuestionLabeler.FallbackAsk(),
    "answer_agent" => AgentQuestionLabeler.FallbackAnswer(),
    _ => CreateLabel(FileIcon(toolName) ?? ToolCallIcon.Generic, fallbackTitle, null, null)
  };

  internal static ToolCallLabel CreateLabel(ToolCallIcon icon, string title, string? detail, string? path) {
    string safeTitle = ToolCallLabelText.OneLine(title, 120);
    string? safeDetail = string.IsNullOrWhiteSpace(detail) ? null : ToolCallLabelText.OneLine(detail, 240);
    if (string.Equals(safeTitle, safeDetail, StringComparison.Ordinal))
      safeDetail = null;
    return new ToolCallLabel(icon, safeTitle, safeDetail, path);
  }
}
