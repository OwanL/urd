using System.Text.Json;

namespace Urd.Core.ToolCallDisplay;

internal static class GenericToolLabeler {
  internal static ToolCallLabel Create(string toolName, JsonElement arguments) =>
    ToolCallLabels.CreateLabel(ToolCallIcon.Generic, toolName, ToolCallExcerpt.RenderCompactArguments(arguments), null);
}
