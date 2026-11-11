using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using Urd.Core.Tools;

namespace Urd.Core.Storage;

// Persists one tool output; images are managed bytes, details remain inspection-only.
internal sealed class ToolResultCodec(ToolImageStore images) {
  private static readonly JsonSerializerOptions json = new(JsonSerializerDefaults.Web) {
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
  };

  internal string Write(ToolOutput output) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(output.Text);
    ArgumentNullException.ThrowIfNull(output.Images);
    // Freeze borrowed JSON before publishing any image; an invalid result fences its caller.
    JsonElement? details = output.Details?.Clone();
    ToolImageReference[] references = output.Images.Select(images.Save).ToArray();
    return JsonSerializer.Serialize(new ToolResultEnvelope(output.Text, references, details, output.IsError), json);
  }

  internal ToolOutput Read(string value) {
    ToolResultEnvelope saved = JsonSerializer.Deserialize<ToolResultEnvelope>(value, json)
        ?? throw new InvalidDataException("Stored tool output is missing.");
    if (saved.Text is null || saved.Images is null)
      throw new InvalidDataException("Stored tool output has an invalid shape. Recreate the local database.");
    return new ToolOutput(saved.Text, saved.Images.Select(images.Read).ToArray(), saved.Details, saved.IsError);
  }

  internal object ReadModelResult(string value) {
    ToolOutput output = Read(value);
    if (output.Images.Count == 0)
      return output.Text;
    return new AIContent[] { new TextContent(output.Text) }.Concat(output.Images).ToArray();
  }

  internal string Error(string message) => Write(new ToolOutput(message, [], IsError: true));

  // This schema replaces the previous result shape; there is deliberately no legacy reader.
  private sealed record ToolResultEnvelope(
    string Text, IReadOnlyList<ToolImageReference> Images, JsonElement? Details, bool IsError);
}
