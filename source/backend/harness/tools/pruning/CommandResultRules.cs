using System.Text.Json;
using System.Text.RegularExpressions;

namespace Urd.Core.Tools;

internal static class CommandResultRules {
  private static readonly TimeSpan regexTimeout = TimeSpan.FromMilliseconds(100);
  private static readonly Regex grepMatch = new(@"^(.+?):(\d+):(.*)$", RegexOptions.None, regexTimeout);
  private static readonly Regex pathy = new(@"[/\\]|\.[A-Za-z0-9]{1,8}$", RegexOptions.None, regexTimeout);

  internal static string GroupMatches(string toolName, JsonElement arguments, string text) {
    if (toolName == "grep") return GrepGroup(text) ?? text;
    if (toolName != "bash" || arguments.ValueKind != JsonValueKind.Object
        || !arguments.TryGetProperty("command", out JsonElement value) || value.ValueKind != JsonValueKind.String)
      return text;
    string? command = value.GetString();
    return command is not null && IsGrep(command) ? GrepGroup(text) ?? text : text;
  }

  private static bool IsGrep(string command) {
    var segments = new List<List<string>> { new() };
    var word = new System.Text.StringBuilder();
    char quote = '\0';
    bool started = false;
    foreach (char c in command) {
      if (c is '\r' or '\n' || (quote != '\'' && c is '\\' or '$' or '`')) return false;
      if (quote != '\0') {
        if (c == quote) quote = '\0';
        else word.Append(c);
        continue;
      }
      if (c is '\'' or '"') { quote = c; started = true; continue; }
      if (c is ';' or '&' or '<' or '>' or '(' or ')' or '{' or '}' or '#') return false;
      if (char.IsWhiteSpace(c) || c == '|') {
        if (started) { segments[^1].Add(word.ToString()); word.Clear(); started = false; }
        if (c == '|') segments.Add(new());
      } else { word.Append(c); started = true; }
    }
    if (quote != '\0') return false;
    if (started) segments[^1].Add(word.ToString());
    List<string> first = segments[0];
    int i = 0;
    if (i < first.Count && first[i] == "time") i++;
    if (i < first.Count && first[i] == "sudo") i++;
    if (i == first.Count) return false;
    if (first[i] == "git") {
      if (++i == first.Count || first[i] != "grep") return false;
    } else if (first[i] is not ("rg" or "ripgrep" or "rga" or "grep" or "egrep" or "fgrep")) return false;
    return segments.Skip(1).All(segment => segment.Count > 0 && (segment[0] is "head" or "sort"));
  }

  private static string? GrepGroup(string text) {
    bool trailing = text.EndsWith('\n');
    string newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
    if (trailing && !text.EndsWith(newline, StringComparison.Ordinal)) return null;
    string body = trailing ? text[..^newline.Length] : text;
    string[] lines = body.Split(newline);
    var output = new List<string>();
    string? current = null;
    foreach (string line in lines) {
      if (line.Length == 0 || line.Contains('\r') || line.Contains('\n') || char.IsWhiteSpace(line[0])) return null;
      Match match = grepMatch.Match(line);
      if (!match.Success || match.Groups[1].Length < 2 || !pathy.IsMatch(match.Groups[1].Value)) return null;
      // A second delimiter could instead belong to the path, so the record has no unique parse.
      if (Regex.IsMatch(line[(match.Groups[3].Index - 1)..], @":\d+:", RegexOptions.None, regexTimeout)) return null;
      string path = match.Groups[1].Value;
      if (path != current) { output.Add(path); current = path; }
      output.Add($"  {match.Groups[2].Value}:{match.Groups[3].Value}");
    }
    string rewritten = string.Join(newline, output) + (trailing ? newline : "");
    return rewritten.Length < text.Length ? rewritten : null;
  }
}
