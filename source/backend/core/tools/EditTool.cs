using System.Text;
using System.Text.Json;

namespace Urd.Core.Tools;

// Matches every replacement against one original snapshot without normalizing untouched text.
internal sealed class EditTool(FileMutationQueue files) : AgentTool {
  private static readonly JsonElement schema = ToolSchema.Parse("""
    {
      "type": "object",
      "properties": {
        "path": { "type": "string", "description": "Path to the file to edit (relative or absolute)" },
        "edits": {
          "type": "array",
          "minItems": 1,
          "description": "Non-overlapping replacements, each matched against the original file, not incrementally.",
          "items": {
            "type": "object",
            "properties": {
              "oldText": { "type": "string", "minLength": 1, "description": "Exact text that is unique in the original file. CRLF and LF are equivalent." },
              "newText": { "type": "string", "description": "Replacement text" }
            },
            "required": ["oldText", "newText"]
          }
        }
      },
      "required": ["path", "edits"]
    }
    """);

  public override string Name => "edit";
  public override string Description => """
    Edit a single file using exact text replacement.
    Every edits[].oldText must match a unique, non-overlapping region of the original file.
    CRLF and LF are equivalent. No fuzzy matching.
    Merge nearby changes into one edit; do not pad with large unchanged regions.
    """;
  public override JsonElement ParametersSchema => schema;

  public override async Task<ToolOutput> RunAsync(JsonElement arguments, ToolContext context, CancellationToken cancellation) {
    try {
      string path = ToolArguments.String(arguments, "path");
      JsonElement edits = arguments.GetProperty("edits");
      if (edits.ValueKind != JsonValueKind.Array || edits.GetArrayLength() == 0)
        throw new ArgumentException("edits must contain at least one replacement.");
      string absolutePath = ToolPaths.Resolve(path, context.WorkingDirectory);
      // Queue before the first suspension, including the initial file read.
      return await files.ChangeAsync(absolutePath, async token => {
        await FileEdits.ApplyAsync(absolutePath, edits, token).ConfigureAwait(false);
        return new ToolOutput($"Successfully replaced {edits.GetArrayLength()} block(s) in {path}.", []);
      }, cancellation).ConfigureAwait(false);
    } catch (OperationCanceledException) {
      return new ToolOutput("edit cancelled. The file may be partly written; cancellation does not roll back changes.", [], IsError: true);
    } catch (DecoderFallbackException) {
      return new ToolOutput("edit failed: file is not valid UTF-8. No changes were written.", [], IsError: true);
    } catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or KeyNotFoundException or NotSupportedException) {
      return new ToolOutput($"edit failed: {error.Message}", [], IsError: true);
    }
  }
}
