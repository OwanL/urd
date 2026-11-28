using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using static Urd.Core.Providers.AnthropicJson;

namespace Urd.Core.Providers;

// Only Claude's portable text, private thinking and client tool calls can be replayed.
internal sealed class AnthropicContentBlock {
  private readonly StringBuilder content = new();
  private readonly StringBuilder signature = new();
  private readonly JsonElement initialInput;
  private int remainingBytes;
  private bool hasInputDelta;

  internal AnthropicContentBlock(JsonElement block, int maximumBytes) {
    remainingBytes = maximumBytes;
    Count(block.GetRawText());
    Type = RequiredString(block, "type");
    switch (Type) {
      case "text":
        if (block.TryGetProperty("citations", out var citations) && citations.ValueKind != JsonValueKind.Null &&
            (citations.ValueKind != JsonValueKind.Array || citations.GetArrayLength() != 0))
          throw Invalid("unsupported Anthropic citations");
        content.Append(RequiredString(block, "text", allowEmpty: true));
        break;
      case "thinking":
        content.Append(RequiredString(block, "thinking", allowEmpty: true));
        signature.Append(RequiredString(block, "signature", allowEmpty: true));
        break;
      case "redacted_thinking":
        content.Append(RequiredString(block, "data"));
        break;
      case "tool_use":
        Id = RequiredString(block, "id");
        Name = RequiredString(block, "name");
        initialInput = block.GetProperty("input");
        if (initialInput.ValueKind != JsonValueKind.Object) throw Invalid("tool input is not an object");
        if (block.TryGetProperty("caller", out var caller) && caller.ValueKind != JsonValueKind.Null &&
            ReadString(caller, "type") != "direct")
          throw Invalid("Anthropic tool call is not a direct client call");
        break;
      default:
        throw Invalid("unsupported Anthropic content block");
    }
  }

  internal string Type { get; }
  internal string? Id { get; }
  internal string? Name { get; }

  internal AIContent? InitialDisplay() => Type switch {
    "text" when content.Length != 0 => new TextContent(content.ToString()),
    "thinking" when content.Length != 0 => new TextReasoningContent(content.ToString()),
    _ => null
  };

  internal (AIContent? Display, string? ToolArguments) Append(JsonElement delta) {
    switch (Type, RequiredString(delta, "type")) {
      case ("text", "text_delta"):
        string text = RequiredString(delta, "text", allowEmpty: true);
        Count(text);
        content.Append(text);
        return (text.Length > 0 ? new TextContent(text) : null, null);
      case ("thinking", "thinking_delta") when signature.Length == 0:
        string thinking = RequiredString(delta, "thinking", allowEmpty: true);
        Count(thinking);
        content.Append(thinking);
        return (thinking.Length > 0 ? new TextReasoningContent(thinking) : null, null);
      case ("thinking", "signature_delta"):
        string fragmentSignature = RequiredString(delta, "signature");
        Count(fragmentSignature);
        signature.Append(fragmentSignature);
        return (null, null);
      case ("tool_use", "input_json_delta") when !initialInput.EnumerateObject().Any():
        string fragment = RequiredString(delta, "partial_json", allowEmpty: true);
        Count(fragment);
        content.Append(fragment);
        hasInputDelta |= fragment.Length != 0;
        return (null, fragment);
      default:
        throw Invalid("unsupported or mismatched Anthropic block delta");
    }
  }

  private void Count(string text) {
    remainingBytes -= Encoding.UTF8.GetByteCount(text);
    if (remainingBytes < 0) throw Invalid("Anthropic content exceeds the retained context limit");
  }

  internal JsonElement Complete() => Type switch {
    "text" => JsonSerializer.SerializeToElement(new { type = Type, text = content.ToString() }),
    "thinking" when signature.Length > 0 => JsonSerializer.SerializeToElement(new {
      type = Type, thinking = content.ToString(), signature = signature.ToString()
    }),
    "thinking" => throw Invalid("missing Anthropic thinking signature"),
    "redacted_thinking" => JsonSerializer.SerializeToElement(new { type = Type, data = content.ToString() }),
    "tool_use" => JsonSerializer.SerializeToElement(new { type = Type, id = Id, name = Name, input = ReadInput() }),
    _ => throw Invalid("unsupported Anthropic content block")
  };

  private JsonElement ReadInput() {
    JsonElement input = hasInputDelta ? JsonSerializer.Deserialize<JsonElement>(content.ToString(), Strict) : initialInput;
    if (input.ValueKind != JsonValueKind.Object) throw Invalid("tool input is not an object");
    return input;
  }

  internal static FunctionCallContent ReadCall(JsonElement block) {
    string id = RequiredString(block, "id");
    string name = RequiredString(block, "name");
    var arguments = block.GetProperty("input");
    var values = arguments.EnumerateObject().ToDictionary(property => property.Name,
        property => (object?)property.Value.Clone(), StringComparer.Ordinal);
    return new(id, name, values);
  }

  internal static void Restore(JsonElement block, IList<AIContent> contents) {
    try {
      var native = new AnthropicContentBlock(block, LlmProviderContext.MaximumBytes);
      switch (native.Type) {
        case "text":
          contents.Add(new TextContent(native.content.ToString()));
          break;
        case "thinking" when native.signature.Length > 0:
          contents.Add(new TextReasoningContent(native.content.ToString()));
          break;
        case "redacted_thinking":
          break; // Retained in the native envelope only.
        case "tool_use":
          contents.Add(ReadCall(block));
          break;
        default:
          throw Invalid("invalid Anthropic replay block");
      }
    } catch (ProviderOperationException) {
      throw new InvalidDataException("Invalid provider context.");
    }
  }
}
