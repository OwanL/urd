using System.Text;
using System.Text.Json;

namespace Urd.Core.Tools;

// Matches every replacement against one original snapshot without normalizing untouched text.
internal sealed class EditTool(FileMutationQueue files) : AgentTool {
  private static readonly UTF8Encoding utf8 = new(false, true);
  private static readonly JsonElement schema = JsonSerializer.SerializeToElement(new {
    type = "object",
    properties = new {
      path = new { type = "string", description = "Path to the file to edit (relative or absolute)" },
      edits = new {
        type = "array", minItems = 1,
        description = "Non-overlapping replacements, each matched against the original file, not incrementally.",
        items = new {
          type = "object",
          properties = new {
            oldText = new { type = "string", minLength = 1, description = "Exact text that is unique in the original file. CRLF and LF are equivalent." },
            newText = new { type = "string", description = "Replacement text" }
          },
          required = new[] { "oldText", "newText" }
        }
      }
    },
    required = new[] { "path", "edits" }
  });

  public override string Name => "edit";
  public override string Description => "Edit a single file using exact text replacement. Every edits[].oldText must match a unique, non-overlapping region of the original file. CRLF and LF are equivalent. No fuzzy matching. Merge nearby changes into one edit; do not pad with large unchanged regions.";
  public override JsonElement ParametersSchema => schema;

  public override async Task<ToolOutput> RunAsync(JsonElement arguments, ToolContext context, CancellationToken cancellation) {
    try {
      string path = arguments.GetProperty("path").GetString() ?? throw new ArgumentException("path must be a string.");
      JsonElement edits = arguments.GetProperty("edits");
      if (edits.ValueKind != JsonValueKind.Array || edits.GetArrayLength() == 0)
        throw new ArgumentException("edits must contain at least one replacement.");
      string absolutePath = ToolPaths.Resolve(path, context.WorkingDirectory);
      // Queue before the first suspension, including the initial file read.
      return await files.ChangeAsync(absolutePath, async token => {
        byte[] original = await File.ReadAllBytesAsync(absolutePath, token).ConfigureAwait(false);
        string text = utf8.GetString(original);
        token.ThrowIfCancellationRequested();
        byte[] changed = Replace(text, edits, token);
        token.ThrowIfCancellationRequested();
        await File.WriteAllBytesAsync(absolutePath, changed, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
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

  private static byte[] Replace(string original, JsonElement edits, CancellationToken cancellation) {
    int bomLength = original.StartsWith('\uFEFF') ? 1 : 0;
    var normalized = new StringBuilder(original.Length);
    var positions = new List<int>(original.Length + 1);
    int index = bomLength;
    while (index < original.Length) {
      positions.Add(index);
      if (original[index] == '\r' && index + 1 < original.Length && original[index + 1] == '\n')
        index++;
      normalized.Append(original[index]);
      index++;
    }
    positions.Add(original.Length);
    string snapshot = normalized.ToString();
    var replacements = new List<Replacement>(edits.GetArrayLength());
    foreach (JsonElement edit in edits.EnumerateArray()) {
      cancellation.ThrowIfCancellationRequested();
      string oldText = edit.GetProperty("oldText").GetString() ?? throw new ArgumentException("oldText must be a string.");
      string newText = edit.GetProperty("newText").GetString() ?? throw new ArgumentException("newText must be a string.");
      oldText = oldText.Replace("\r\n", "\n", StringComparison.Ordinal);
      if (oldText.Length == 0)
        throw new ArgumentException("oldText must not be empty.");
      int start = snapshot.IndexOf(oldText, StringComparison.Ordinal);
      if (start < 0)
        throw new ArgumentException("oldText was not found in the original file. No changes were written.");
      if (snapshot.IndexOf(oldText, start + 1, StringComparison.Ordinal) >= 0)
        throw new ArgumentException("oldText matches more than once in the original file. Include more context. No changes were written.");
      int end = start + oldText.Length;
      int originalStart = positions[start];
      int originalEnd = positions[end];
      string ending = LineEnding(original.AsSpan(originalStart, originalEnd - originalStart)) ?? LineEnding(original) ?? "\n";
      string replacement = newText.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", ending, StringComparison.Ordinal);
      replacements.Add(new Replacement(originalStart, originalEnd, replacement));
    }
    replacements.Sort((left, right) => left.Start.CompareTo(right.Start));
    var result = new StringBuilder(original.Length);
    int cursor = 0;
    foreach (Replacement replacement in replacements) {
      if (replacement.Start < cursor)
        throw new ArgumentException("Replacement regions overlap in the original file. Merge them into one edit. No changes were written.");
      result.Append(original.AsSpan(cursor, replacement.Start - cursor));
      result.Append(replacement.Text);
      cursor = replacement.End;
    }
    result.Append(original.AsSpan(cursor));
    return utf8.GetBytes(result.ToString());
  }

  private static string? LineEnding(ReadOnlySpan<char> text) {
    int newline = text.IndexOf('\n');
    if (newline < 0)
      return null;
    return newline > 0 && text[newline - 1] == '\r' ? "\r\n" : "\n";
  }

  // Original UTF-16 boundaries and the replacement to splice between untouched spans.
  private sealed record Replacement(int Start, int End, string Text);
}
