using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using Urd.Core.Execution;
using Urd.Core.Providers;
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

  internal string ReadModelResultText(
      TextReader reader, int? characterLimit, bool supportsImages, out long length) {
    if (characterLimit is { } limit)
      return ToolTranscriptPreview.ReadResult(reader, limit, supportsImages, string.Empty, out length);
    string text = ProjectModelResult(reader.ReadToEnd(), loadImages: false, supportsImages: supportsImages).Text;
    length = text.Length;
    return text;
  }

  internal static string BackgroundStarted(Guid requestId, string callId) =>
      "Background tool started: " + JsonSerializer.Serialize(new { request_id = requestId, call_id = callId })
      + ". The result will be delivered separately.";

  internal List<AIContent> ReadModelCompletion(
      string callId, string name, ExecutionOutcome outcome, string resultJson) {
    ModelToolResult result = ProjectModelResult(resultJson, loadImages: true);
    var contents = new List<AIContent> { new TextContent(CompletionHeader(callId, name, outcome)) };
    contents.Add(new TextContent(result.TextWithoutImages));
    contents.AddRange(result.Images);
    return contents;
  }

  internal string ReadModelCompletionText(string callId, string name, ExecutionOutcome outcome,
      TextReader reader, int? characterLimit, bool supportsImages, out long length) {
    string prefix = CompletionHeader(callId, name, outcome);
    if (characterLimit is { } limit)
      return ToolTranscriptPreview.ReadResult(reader, limit, supportsImages, prefix, out length);
    string text = prefix + ProjectModelResult(reader.ReadToEnd(), loadImages: false, supportsImages: supportsImages).Text;
    length = text.Length;
    return text;
  }

  internal string Error(string message) => Write(new ToolOutput(message, [], IsError: true));

  private ModelToolResult ProjectModelResult(string value, bool loadImages, bool supportsImages = true) {
    ToolResultEnvelope saved = ReadEnvelope(value);
    var projectedImages = new List<AIContent>(saved.Images.Count);
    string text = saved.Text;
    foreach (ToolImageReference image in saved.Images) {
      if (loadImages)
        projectedImages.Add(images.Read(image));
      else if (supportsImages)
        projectedImages.Add(new TextContent(ImagePlaceholder(image)));
      if (supportsImages)
        text = AppendImagePlaceholder(text, image);
    }
    if (saved.Images.Count > 0 && !supportsImages)
      text = LlmMessageProjection.AppendImageOmissionNotice(text);

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

  private static string CompletionHeader(string callId, string name, ExecutionOutcome outcome) {
    string status = outcome switch {
      ExecutionOutcome.Completed => string.Empty,
      ExecutionOutcome.Failed => " (failed)",
      ExecutionOutcome.Cancelled => " (cancelled)",
      _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null)
    };
    return $"Results from background {HeaderField(name)} call, ID: {HeaderField(callId)}{status}\n";
  }

  private static string HeaderField(string value) => value.Any(c => char.IsControl(c) || c is '\u2028' or '\u2029')
      ? JsonEncodedText.Encode(value).ToString() : value;

  private static string AppendImagePlaceholder(string text, ToolImageReference image) {
    string placeholder = ImagePlaceholder(image);
    return text.Length == 0 ? placeholder : text + "\n" + placeholder;
  }

  private static string ImagePlaceholder(ToolImageReference image) => $"[image: {image.MediaType}]";

  // Text/images stay first and ordered for bounded transcript reads; details are never model-facing.
  // This schema replaces the previous result shape; there is deliberately no legacy reader.
  private sealed record ToolResultEnvelope(
    string Text, IReadOnlyList<ToolImageReference> Images, JsonElement? Details, bool IsError);

  private sealed record ModelToolResult(
    object Content, string TextWithoutImages, IReadOnlyList<AIContent> Images, string Text);
}
