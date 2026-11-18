using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Urd.Core.Tools;

internal static class TextResultRules {
  private static readonly TimeSpan regexTimeout = TimeSpan.FromSeconds(1);
  private static readonly Regex ansi = new(@"[\u001B\u009B][\[\]()#;?]*(?:(?:(?:[a-zA-Z\d]*(?:;[-a-zA-Z\d/#&.:=?%@~_]*)*)?\u0007)|(?:(?:\d{1,4}(?:;\d{0,4})*)?[\dA-PR-TZcf-nq-uy=><~]))", RegexOptions.CultureInvariant, regexTimeout);
  private static readonly Regex severity = new(@"\b(?:error|warn|warning|fatal|fail|failed|failure|exception|panic|notice)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.ECMAScript, regexTimeout);
  private static readonly Regex bracketedPercent = new(@"^\s*[\(\[].*%[\)\]]", RegexOptions.CultureInvariant, regexTimeout);

  internal static string Normalize(string text) {
    text = ansi.Replace(text, "").Replace("\r\n", "\n", StringComparison.Ordinal);
    text = CollapseBlankRuns(text);
    return MinifyJson(text);
  }

  internal static string Reduce(string text, List<string> applied) {
    string reduced = RemoveProgress(text);
    if (reduced != text) {
      applied.Add("progress-noise");
      text = reduced;
    }
    reduced = CollapseDuplicates(text);
    if (reduced != text) {
      applied.Add("duplicate-collapse");
      text = reduced;
    }
    return text;
  }

  private static string CollapseBlankRuns(string text) {
    if (!text.Contains('\n')) return text;
    string[] lines = text.Split('\n');
    int first = Array.FindIndex(lines, line => line.Length != 0);
    if (first < 0) return "\n";
    int last = Array.FindLastIndex(lines, line => line.Length != 0);
    int run = 0;
    bool longRun = false;
    foreach (string line in lines) {
      run = line.Length == 0 ? run + 1 : 0;
      if (run >= 3) longRun = true;
    }
    if (!longRun && first == 0 && lines.Length - 1 - last <= 1) return text;
    var output = new List<string>();
    run = 0;
    for (int i = first; i < lines.Length; i++) {
      if (lines[i].Length == 0) {
        if (++run == 1) output.Add("");
      } else {
        run = 0;
        output.Add(lines[i]);
      }
    }
    return string.Join('\n', output);
  }

  private static string MinifyJson(string text) {
    string trimmed = text.Trim();
    if (trimmed.Length == 0 || (trimmed[0] != '{' && trimmed[0] != '[')) return text;
    try {
      using JsonDocument document = JsonDocument.Parse(trimmed);
    } catch (JsonException) {
      return text;
    }
    var output = new StringBuilder(trimmed.Length);
    bool inString = false;
    bool escaped = false;
    foreach (char c in trimmed) {
      if (inString) {
        output.Append(c);
        if (escaped) escaped = false;
        else if (c == '\\') escaped = true;
        else if (c == '"') inString = false;
      } else if (c == '"') {
        inString = true;
        output.Append(c);
      } else if (c is not (' ' or '\t' or '\r' or '\n')) {
        output.Append(c);
      }
    }
    if (text.EndsWith('\n')) output.Append('\n');
    return output.ToString();
  }

  private static string RemoveProgress(string text) {
    if (!text.Contains('\n')) return text;
    bool trailing = text.EndsWith('\n');
    string normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal);
    string body = trailing ? normalized[..^1] : normalized;
    string[] lines = body.Split('\n');
    var kept = new List<string>(lines.Length);
    bool removed = false;
    foreach (string line in lines) {
      if (IsProgress(line)) removed = true;
      else kept.Add(line);
    }
    if (!removed || !kept.Any(line => !string.IsNullOrWhiteSpace(line))) return text;
    string result = string.Join('\n', kept) + (trailing ? "\n" : "");
    return result.Length < text.Length ? result : text;
  }

  private static bool IsProgress(string line) {
    if (string.IsNullOrWhiteSpace(line) || severity.IsMatch(line)) return false;
    if (line.Any(c => c is >= '\u2800' and <= '\u28ff')) return true;
    return line.Contains('%') && (line.Any(c => c is >= '\u2588' and <= '\u259f' or >= '\u25a0' and <= '\u25a9') || bracketedPercent.IsMatch(line));
  }

  private static string CollapseDuplicates(string text) {
    if (!text.Contains('\n')) return text;
    bool trailing = text.EndsWith('\n');
    string normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal);
    string body = trailing ? normalized[..^1] : normalized;
    string[] lines = body.Split('\n');
    var output = new List<string>(lines.Length);
    int removed = 0;
    for (int i = 0; i < lines.Length;) {
      string line = lines[i];
      int end = i + 1;
      if (!string.IsNullOrWhiteSpace(line) && !severity.IsMatch(line)) {
        while (end < lines.Length && lines[end] == line) end++;
      }
      int count = end - i;
      output.Add(line);
      if (count >= 3) {
        output.Add($"  (... {count - 1} identical lines)");
        removed += count - 1;
      } else if (count == 2) output.Add(line);
      i = end;
    }
    if (removed == 0) return text;
    string result = string.Join('\n', output) + (trailing ? "\n" : "");
    return result.Length < text.Length ? result : text;
  }
}
