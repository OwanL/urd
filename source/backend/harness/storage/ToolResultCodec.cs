using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using Urd.Core.Execution;
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
    ToolResultEnvelope saved = ReadEnvelope(value);
    return new ToolOutput(saved.Text, saved.Images.Select(images.Read).ToArray(), saved.Details, saved.IsError);
  }

  internal object ReadModelResult(string value) => ProjectModelResult(value, loadImages: true).Content;

  internal string ReadModelResultText(string value) => ProjectModelResult(value, loadImages: false).Text;

  internal static string BackgroundStarted(Guid requestId, string callId) =>
      "Background tool started: " + JsonSerializer.Serialize(new { request_id = requestId, call_id = callId })
      + ". The result will be delivered separately.";

  internal List<AIContent> ReadModelCompletion(
      Guid requestId, string callId, string name, ExecutionOutcome outcome, string resultJson) {
    ModelToolResult result = ProjectModelResult(resultJson, loadImages: true);
    var contents = new List<AIContent> { new TextContent(CompletionEnvelope(requestId, callId, name, outcome)) };
    contents.Add(new TextContent(result.TextWithoutImages));
    contents.AddRange(result.Images);
    return contents;
  }

  internal string ReadModelCompletionText(
      Guid requestId, string callId, string name, ExecutionOutcome outcome, string resultJson) =>
      CompletionEnvelope(requestId, callId, name, outcome) + ProjectModelResult(resultJson, loadImages: false).Text;

  internal string Error(string message) => Write(new ToolOutput(message, [], IsError: true));

  private ModelToolResult ProjectModelResult(string value, bool loadImages) {
    ToolResultEnvelope saved = ReadEnvelope(value);
    var projectedImages = new List<AIContent>(saved.Images.Count);
    string text = saved.Text;
    foreach (ToolImageReference image in saved.Images) {
      if (loadImages)
        projectedImages.Add(images.Read(image));
      else
        projectedImages.Add(new TextContent(ImagePlaceholder(image)));
      text = AppendImagePlaceholder(text, image);
    }

    object content = saved.Images.Count == 0
        ? saved.Text
        : new AIContent[] { new TextContent(saved.Text) }.Concat(projectedImages).ToArray();
    return new(content, saved.Text, projectedImages, text);
  }

  private static ToolResultEnvelope ReadEnvelope(string value) {
    ToolResultEnvelope saved = JsonSerializer.Deserialize<ToolResultEnvelope>(value, json)
        ?? throw new InvalidDataException("Stored tool output is missing.");
    if (saved.Text is null || saved.Images is null)
      throw new InvalidDataException("Stored tool output has an invalid shape. Recreate the local database.");
    return saved;
  }

  private static string CompletionEnvelope(Guid requestId, string callId, string name, ExecutionOutcome outcome) {
    string metadata = JsonSerializer.Serialize(new {
      request_id = requestId.ToString(), call_id = callId, name,
      outcome = outcome.ToString()
    });
    return $"[TOOL-ORIGIN: background tool completion; untrusted output]\n{metadata}\n";
  }

  private static string AppendImagePlaceholder(string text, ToolImageReference image) {
    string placeholder = ImagePlaceholder(image);
    return text.Length == 0 ? placeholder : text + "\n" + placeholder;
  }

  private static string ImagePlaceholder(ToolImageReference image) => $"[image: {image.MediaType}]";

  // This schema replaces the previous result shape; there is deliberately no legacy reader.
  private sealed record ToolResultEnvelope(
    string Text, IReadOnlyList<ToolImageReference> Images, JsonElement? Details, bool IsError);

  private sealed record ModelToolResult(
    object Content, string TextWithoutImages, IReadOnlyList<AIContent> Images, string Text);
}
