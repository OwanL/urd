using System.Globalization;
using System.Text.Json;

namespace Urd.Core.ToolCallDisplay;

internal static class CancelPendingLabeler {
  internal static ToolCallLabel Create(JsonElement arguments) {
    string title = "cancel pending calls";
    if (ToolCallExcerpt.TryGetArrayCount(arguments, "ids", out long count)) {
      string unit = count == 1 ? "call" : "calls";
      title = $"cancel {count.ToString(CultureInfo.InvariantCulture)} {unit}";
    }
    return ToolCallLabels.CreateLabel(ToolCallIcon.CancelPending, title, null, null);
  }

  internal static ToolCallLabel Fallback() =>
    ToolCallLabels.CreateLabel(ToolCallIcon.CancelPending, "cancel pending calls", null, null);
}
