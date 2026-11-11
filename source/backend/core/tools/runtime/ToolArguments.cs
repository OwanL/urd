using System.Text.Json;

namespace Urd.Core.Tools;

// Required strings keep the same validation and error wording across tool contracts.
internal static class ToolArguments {
  public static string String(JsonElement arguments, string name) =>
      arguments.GetProperty(name).GetString() ?? throw new ArgumentException($"{name} must be a string.");
}
