using System.Text.Json;

namespace Urd.Core.Tools;

// Browser-specific argument reads: addresses, page ids and nonempty element references.
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
      string kind = ToolArguments.OptionalString(entry, "type") ?? "text";
      if (kind is not ("text" or "checkbox" or "select"))
        throw new ArgumentException($"Unknown form field type \"{kind}\"; expected text, checkbox or select.");
      fields.Add(new FormField(Require(entry, "ref"), kind, Require(entry, "value")));
    }
    if (fields.Count == 0)
      throw new ArgumentException("form must list at least one field.");
    return [.. fields];
  }

  // Element references, action names and addresses must be nonempty.
  private static string Require(JsonElement element, string name) {
    string value = ToolArguments.RequireString(element, name);
    if (value.Length == 0)
      throw new ArgumentException($"{name} is required and must be a nonempty string.");
    return value;
  }

  private static int RequireMilliseconds(JsonElement value) {
    if (value.ValueKind is not JsonValueKind.Number || !value.TryGetInt32(out int milliseconds))
      throw new ArgumentException("ms must be a number.");
    return milliseconds;
  }

  // One fill_form field; its kind selects how the value is applied to the element.
  private sealed record FormField(string Ref, string Kind, string Value);
}
