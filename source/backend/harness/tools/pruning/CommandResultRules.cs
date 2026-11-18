using System.Text.Json;
using System.Text.RegularExpressions;

namespace Urd.Core.Tools;

internal static class CommandResultRules {
  private static readonly TimeSpan regexTimeout = TimeSpan.FromMilliseconds(100);
  private static readonly Regex lsEntry = new(@"^([dlsbcpst-])\S*[ \t]+\S+[ \t]+\S+[ \t]+\S+[ \t]+\S+[ \t]+(?:Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec)[ \t]+\d{1,2}[ \t]+(?:\d{1,2}:\d{2}|\d{4})[ \t](.+)$", RegexOptions.None, regexTimeout);
  private static readonly Regex commit = new(@"^commit ([0-9a-f]{7,40})(?: \((.+)\))?$", RegexOptions.None, regexTimeout);
  private static readonly Regex grepMatch = new(@"^(.+?):(\d+):(.*)$", RegexOptions.None, regexTimeout);
  private static readonly Regex pathy = new(@"[/\\]|\.[A-Za-z0-9]{1,8}$", RegexOptions.None, regexTimeout);
  private static readonly Regex simpleCommand = new("[|;&<>\\r\\n$`\\\\'\"(){}#]", RegexOptions.None, regexTimeout);
  private static readonly Regex lsOption = new(@"^-[A-Za-z]*l[A-Za-z]*$", RegexOptions.None, regexTimeout);
  private static readonly Regex lsTotal = new(@"^total[ \t]+\d+$", RegexOptions.None, regexTimeout);
  private static readonly Regex gitLogOption = new(@"^(?:-\d+|--max-count=\d+|--all|--branches|--tags)$", RegexOptions.None, regexTimeout);
  private static readonly Regex lineBreaks = new("\\r?\\n", RegexOptions.None, regexTimeout);

  internal static string Summarize(string toolName, JsonElement arguments, string text, List<string> applied) {
    string? command = arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("command", out JsonElement value)
        && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    string? result = null;
    string? rule = null;
    if (toolName == "bash" && command is not null) {
      if (IsLsLong(command)) {
        result = LsLong(text);
        rule = "ls-long";
      } else if (IsGitLog(command)) {
        result = GitLog(text);
        rule = "git-log";
      }
    }
    if (result is null || result == text)
      return text;
    applied.Add(rule!);
    return result;
  }

  internal static string GroupMatches(string toolName, JsonElement arguments, string text) {
    if (toolName == "grep") return GrepGroup(text) ?? text;
    if (toolName != "bash" || arguments.ValueKind != JsonValueKind.Object
        || !arguments.TryGetProperty("command", out JsonElement value) || value.ValueKind != JsonValueKind.String)
      return text;
    string? command = value.GetString();
    return command is not null && IsGrep(command) ? GrepGroup(text) ?? text : text;
  }

  private static bool SimpleCommand(string command) => !simpleCommand.IsMatch(command);

  private static bool IsLsLong(string command) {
    if (!SimpleCommand(command)) return false;
    string[] words = command.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
    if (words.Length == 0 || words[0] != "ls") return false;
    foreach (string word in words.Skip(1)) {
      if (word == "--") break;
      if (word is "--format=long" or "--format=verbose" || lsOption.IsMatch(word))
        return true;
    }
    return false;
  }

  private static string? LsLong(string text) {
    var entries = new List<string>();
    foreach (string line in lineBreaks.Split(text)) {
      if (string.IsNullOrWhiteSpace(line) || lsTotal.IsMatch(line)) continue;
      Match match = lsEntry.Match(line);
      if (!match.Success) return null;
      entries.Add(match.Groups[2].Value + (match.Groups[1].Value == "d" ? "/" : ""));
    }
    return entries.Count == 0 ? null : string.Join("\n", entries) + "\n";
  }

  // Restrict options to list selection; custom formats and diff options can carry requested detail.
  private static bool IsGitLog(string command) {
    if (!SimpleCommand(command)) return false;
    string[] words = command.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
    if (words.Length < 2 || words[0] != "git" || words[1] != "log") return false;
    return words.Skip(2).All(word => gitLogOption.IsMatch(word));
  }

  private static string? GitLog(string text) {
    string[] lines = lineBreaks.Split(text);
    var output = new List<string>();
    int i = 0;
    while (i < lines.Length) {
      if (lines[i].Length == 0) { i++; continue; }
      Match commitMatch = commit.Match(lines[i++]);
      if (!commitMatch.Success) return null;
      bool author = false, date = false;
      while (i < lines.Length && lines[i].Length != 0) {
        string header = lines[i++];
        if (header.StartsWith("Author: ", StringComparison.Ordinal)) author = true;
        else if (header.StartsWith("Date: ", StringComparison.Ordinal)) date = true;
        else if (!header.StartsWith("Merge: ", StringComparison.Ordinal)) return null;
      }
      if (!author || !date || i == lines.Length) return null;
      while (i < lines.Length && lines[i].Length == 0) i++;
      if (i == lines.Length || !lines[i].StartsWith("    ", StringComparison.Ordinal)) return null;
      string subject = lines[i++].Trim();
      if (subject.Length == 0) return null;
      while (i < lines.Length && !commit.IsMatch(lines[i])) {
        if (lines[i].Length != 0 && !lines[i].StartsWith("    ", StringComparison.Ordinal)) return null;
        i++;
      }
      string refs = commitMatch.Groups[2].Success ? $" ({commitMatch.Groups[2].Value})" : "";
      output.Add($"{commitMatch.Groups[1].Value[..7]} {subject}{refs}");
    }
    return output.Count == 0 ? null : string.Join("\n", output) + "\n";
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
