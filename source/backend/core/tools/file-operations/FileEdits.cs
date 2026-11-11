using System.Text;
using System.Text.Json;

namespace Urd.Core.Tools;

// Applies exact replacements against one snapshot, preserving untouched bytes and line endings.
internal static class FileEdits {
  private static readonly UTF8Encoding utf8 = new(false, true);

  internal static async Task ApplyAsync(string path, JsonElement edits, CancellationToken cancellation) {
    byte[] original = await File.ReadAllBytesAsync(path, cancellation).ConfigureAwait(false);
    string text = utf8.GetString(original);
    cancellation.ThrowIfCancellationRequested();
    byte[] changed = Replace(text, edits, cancellation);
    cancellation.ThrowIfCancellationRequested();
    await File.WriteAllBytesAsync(path, changed, cancellation).ConfigureAwait(false);
    cancellation.ThrowIfCancellationRequested();
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
      string oldText = ToolArguments.String(edit, "oldText");
      string newText = ToolArguments.String(edit, "newText");
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
