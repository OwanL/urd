using System.Text;
using System.Text.Json;

namespace Urd.Core.ToolCallDisplay;

internal static class ToolCallExcerpt {
  internal static bool TryParse(string? text, out JsonDocument? document) {
    document = null;
    if (text is null || text.Length > 4096)
      return false;

    try {
      JsonDocument parsed = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 2048 });
      if (parsed.RootElement.ValueKind != JsonValueKind.Object) {
        parsed.Dispose();
        return false;
      }
      document = parsed;
      return true;
    } catch (JsonException) {
      return false;
    }
  }

  internal static bool TryGetProperty(JsonElement? value, string name, out JsonElement property) {
    if (value is JsonElement element && element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out property))
      return true;
    property = default;
    return false;
  }

  internal static bool TryGetExactString(JsonElement? value, string name, out string text) {
    if (TryGetProperty(value, name, out JsonElement property) && property.ValueKind == JsonValueKind.String) {
      text = property.GetString()!;
      return true;
    }
    text = "";
    return false;
  }

  internal static bool TryGetPositiveInteger(JsonElement? value, string name, out int number) {
    if (TryGetProperty(value, name, out JsonElement property) && property.ValueKind == JsonValueKind.Number &&
        property.TryGetInt32(out number) && number > 0)
      return true;
    number = 0;
    return false;
  }

  internal static bool IsTruncated(JsonElement value) =>
    value.ValueKind == JsonValueKind.Object && value.TryGetProperty("$truncated", out _);

  internal static bool HasTopLevelOmitted(JsonElement value) => value.TryGetProperty("$omitted", out _);

  internal static bool TryGetArrayCount(JsonElement? value, string name, out long count) {
    count = 0;
    if (!TryGetProperty(value, name, out JsonElement array) || array.ValueKind != JsonValueKind.Array)
      return false;

    foreach (JsonElement item in array.EnumerateArray()) {
      if (TryGetOmittedCount(item, out long omitted)) {
        try {
          count = checked(count + omitted);
        } catch (OverflowException) {
          return false;
        }
        continue;
      }
      if (count == long.MaxValue)
        return false;
      count++;
    }
    return true;
  }

  internal static bool TryGetOmittedCount(JsonElement value, out long count) {
    count = 0;
    return value.ValueKind == JsonValueKind.Object && value.TryGetProperty("$omitted", out JsonElement omitted) &&
      omitted.ValueKind == JsonValueKind.Number && omitted.TryGetInt64(out count) && count >= 0;
  }

  internal static string? RenderCompactArguments(JsonElement arguments) {
    var text = new StringBuilder();
    foreach (JsonProperty property in arguments.EnumerateObject()) {
      if (text.Length > 0)
        text.Append(", ");
      AppendCompactProperty(text, property, 0);
    }
    return text.Length == 0 ? null : text.ToString();
  }

  private static void AppendCompactProperty(StringBuilder text, JsonProperty property, int depth) {
    AppendSingleLine(text, property.Name);
    text.Append('=');
    if (property.Name == "$omitted")
      text.Append('…');
    else
      AppendCompactValue(text, property.Value, depth);
  }

  private static void AppendCompactValue(StringBuilder text, JsonElement value, int depth) {
    if (IsTruncated(value) || TryGetOmittedCount(value, out _)) {
      text.Append('…');
      return;
    }

    switch (value.ValueKind) {
      case JsonValueKind.String:
        AppendSingleLine(text, value.GetString()!);
        break;
      case JsonValueKind.Array:
        if (depth >= 4) {
          text.Append('…');
          break;
        }
        text.Append('[');
        bool firstItem = true;
        foreach (JsonElement item in value.EnumerateArray()) {
          if (!firstItem)
            text.Append(", ");
          AppendCompactValue(text, item, depth + 1);
          firstItem = false;
        }
        text.Append(']');
        break;
      case JsonValueKind.Object:
        if (depth >= 4) {
          text.Append('…');
          break;
        }
        text.Append('{');
        bool firstProperty = true;
        foreach (JsonProperty property in value.EnumerateObject()) {
          if (!firstProperty)
            text.Append(", ");
          AppendCompactProperty(text, property, depth + 1);
          firstProperty = false;
        }
        text.Append('}');
        break;
      case JsonValueKind.Number:
        text.Append(value.GetRawText());
        break;
      case JsonValueKind.True:
        text.Append("true");
        break;
      case JsonValueKind.False:
        text.Append("false");
        break;
      default:
        text.Append("null");
        break;
    }
  }

  private static void AppendSingleLine(StringBuilder text, string value) {
    int start = text.Length;
    bool previousWasSpace = true;
    foreach (char character in value) {
      bool isSpace = char.IsWhiteSpace(character) || char.IsControl(character);
      if (isSpace) {
        if (!previousWasSpace)
          text.Append(' ');
        previousWasSpace = true;
        continue;
      }
      text.Append(character);
      previousWasSpace = false;
    }
    if (text.Length > start && text[^1] == ' ')
      text.Length--;
  }
}
