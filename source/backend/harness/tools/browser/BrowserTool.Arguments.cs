using System.Text.Json;

namespace Urd.Core.Tools;

// Reads and validates the model's tool arguments.
internal sealed partial class BrowserTool {
  private static string Address(JsonElement arguments) {
    string value = Require(arguments, "url");
    if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? url) || url.Scheme is not ("http" or "https") || url.Host.Length == 0)
      throw new ArgumentException("url must be an absolute http(s) address.");
    return url.ToString();
  }

  private static Guid ParsePageId(string value) {
    if (!Guid.TryParse(value, out Guid id))
      throw new ArgumentException($"\"{value}\" is not a page id from the tabs listing.");
    return id;
  }

  private static FormField[] RequireForm(JsonElement arguments) {
    if (!arguments.TryGetProperty("form", out JsonElement form) || form.ValueKind is not JsonValueKind.Array)
      throw new ArgumentException("form must be an array of fields.");
    List<FormField> fields = [];
    foreach (JsonElement entry in form.EnumerateArray()) {
      string kind = Optional(entry, "type") ?? "text";
      if (kind is not ("text" or "checkbox" or "select"))
        throw new ArgumentException($"Unknown form field type \"{kind}\"; expected text, checkbox or select.");
      fields.Add(new FormField(Require(entry, "ref"), kind, Require(entry, "value")));
    }
    if (fields.Count == 0)
      throw new ArgumentException("form must list at least one field.");
    return [.. fields];
  }

  private static string Require(JsonElement element, string name) {
    if (!element.TryGetProperty(name, out JsonElement value) || value.ValueKind is not JsonValueKind.String
        || value.GetString() is not { } text || text.Length == 0)
      throw new ArgumentException($"{name} is required and must be a nonempty string.");
    return text;
  }

  private static string? Optional(JsonElement element, string name) =>
      element.TryGetProperty(name, out JsonElement value) && value.ValueKind is JsonValueKind.String
          ? value.GetString()
          : null;

  private static bool OptionalBool(JsonElement element, string name) {
    if (!element.TryGetProperty(name, out JsonElement value) || value.ValueKind is JsonValueKind.Null)
      return false;
    if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
      throw new ArgumentException($"{name} must be a boolean.");
    return value.GetBoolean();
  }

  private static string[] RequireStrings(JsonElement element, string name) {
    if (!element.TryGetProperty(name, out JsonElement value) || value.ValueKind is not JsonValueKind.Array)
      throw new ArgumentException($"{name} must be an array of strings.");
    string[] items = value.EnumerateArray().Select(item => item.ValueKind is JsonValueKind.String ? item.GetString() ?? "" : "").ToArray();
    if (items.Any(string.IsNullOrEmpty))
      throw new ArgumentException($"{name} must not contain empty strings.");
    return items;
  }

  private static int RequireInt(JsonElement element, string name) {
    if (!element.TryGetProperty(name, out JsonElement value) || value.ValueKind is not JsonValueKind.Number)
      throw new ArgumentException($"{name} is required and must be a number.");
    return value.GetInt32();
  }

  private static int RequireInt(JsonElement value) {
    if (value.ValueKind is not JsonValueKind.Number)
      throw new ArgumentException("ms must be a number.");
    return value.GetInt32();
  }

  // One fill_form field; its kind selects how the value is applied to the element.
  private sealed record FormField(string Ref, string Kind, string Value);
}
