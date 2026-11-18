using System.Text.Json;

namespace Urd.Core.ToolCallDisplay;

internal static class AgentQuestionLabeler {
  internal static ToolCallLabel CreateAsk(JsonElement arguments) {
    if (!TryGetText(arguments, "recipient", out string recipient, out bool recipientTruncated) ||
        string.IsNullOrWhiteSpace(recipient) ||
        !TryGetText(arguments, "prompt", out string prompt, out bool promptTruncated))
      return FallbackAsk();

    return ToolCallLabels.CreateLabel(ToolCallIcon.AskAgent,
        FormatExcerpt(recipient, recipientTruncated), FormatExcerpt(prompt, promptTruncated), null);
  }

  internal static ToolCallLabel CreateAnswer(JsonElement arguments) {
    if (!TryGetText(arguments, "answer", out string answer, out bool truncated))
      return FallbackAnswer();

    return ToolCallLabels.CreateLabel(ToolCallIcon.AnswerAgent, "answer", FormatExcerpt(answer, truncated), null);
  }

  internal static ToolCallLabel FallbackAsk() =>
    ToolCallLabels.CreateLabel(ToolCallIcon.AskAgent, "ask_agent", null, null);

  internal static ToolCallLabel FallbackAnswer() =>
    ToolCallLabels.CreateLabel(ToolCallIcon.AnswerAgent, "answer_agent", null, null);

  private static bool TryGetText(JsonElement arguments, string name, out string text, out bool truncated) {
    text = "";
    truncated = false;
    if (!ToolCallExcerpt.TryGetProperty(arguments, name, out JsonElement value))
      return false;
    if (value.ValueKind == JsonValueKind.String) {
      text = value.GetString()!;
      return true;
    }
    if (value.ValueKind != JsonValueKind.Object ||
        !value.TryGetProperty("$truncated", out JsonElement prefix) || prefix.ValueKind != JsonValueKind.String)
      return false;

    text = prefix.GetString()!;
    truncated = true;
    return true;
  }

  private static string FormatExcerpt(string text, bool truncated) => truncated ? text + "…" : text;
}
