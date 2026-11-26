using System.Globalization;
using System.Text.Json;

namespace Urd.Core.Tools;

// Shared reads of model tool arguments; every failure names the argument in a readable tool error.
internal static class ToolArguments {
  public static string RequireString(JsonElement arguments, string name) {
    if (!arguments.TryGetProperty(name, out JsonElement value))
      throw new ArgumentException($"{name} is required.");
    if (value.ValueKind is not JsonValueKind.String)
      throw new ArgumentException($"{name} must be a string.");
    return value.GetString()!;
  }

  public static string? OptionalString(JsonElement arguments, string name) {
    if (!arguments.TryGetProperty(name, out JsonElement value) || value.ValueKind is JsonValueKind.Null)
      return null;
    if (value.ValueKind is not JsonValueKind.String)
      throw new ArgumentException($"{name} must be a string.");
    return value.GetString();
  }

  public static bool RequireBool(JsonElement arguments, string name) {
    if (!arguments.TryGetProperty(name, out JsonElement value))
      throw new ArgumentException($"{name} is required.");
    if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
      throw new ArgumentException($"{name} must be a boolean.");
    return value.GetBoolean();
  }

  public static bool OptionalBool(JsonElement arguments, string name, bool fallback) {
    if (!arguments.TryGetProperty(name, out JsonElement value) || value.ValueKind is JsonValueKind.Null)
      return fallback;
    if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
      throw new ArgumentException($"{name} must be a boolean.");
    return value.GetBoolean();
  }

  public static int RequireInteger(JsonElement arguments, string name) {
    if (!arguments.TryGetProperty(name, out JsonElement value))
      throw new ArgumentException($"{name} is required.");
    if (value.ValueKind is not JsonValueKind.Number || !value.TryGetInt32(out int number))
      throw new ArgumentException($"{name} must be an integer.");
    return number;
  }

  public static int OptionalInteger(JsonElement arguments, string name, int minimum, int fallback) {
    if (!arguments.TryGetProperty(name, out JsonElement value))
      return fallback;
    if (value.ValueKind is not JsonValueKind.Number || !value.TryGetInt32(out int number) || number < minimum)
      throw new ArgumentException($"{name} must be an integer of at least {minimum.ToString(CultureInfo.InvariantCulture)}.");
    return number;
  }

  public static string[] RequireStrings(JsonElement arguments, string name) {
    if (!arguments.TryGetProperty(name, out JsonElement value) || value.ValueKind is not JsonValueKind.Array)
      throw new ArgumentException($"{name} must be an array of strings.");
    string[] items = value.EnumerateArray()
        .Select(item => item.ValueKind is JsonValueKind.String ? item.GetString() ?? "" : "").ToArray();
    if (items.Any(string.IsNullOrEmpty))
      throw new ArgumentException($"{name} must not contain empty strings.");
    return items;
  }
}
