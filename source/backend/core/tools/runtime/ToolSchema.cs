using System.Text.Json;

namespace Urd.Core.Tools;

// Tool declarations parse their JSON once and retain an independently owned element.
internal static class ToolSchema {
  public static JsonElement Parse(string json) => JsonSerializer.Deserialize<JsonElement>(json);
}
