using System.Text;
using System.Text.Json;

namespace Urd.Core.ToolCallDisplay;

internal static class BashLabeler {
  private readonly record struct ShellWord(string Value, int End);
  private readonly record struct Segment(int Start, int End);

  internal static ToolCallLabel Create(JsonElement arguments) {
    if (!TryGetCommand(arguments, out string command, out bool truncated))
      return ToolCallLabels.CreateLabel(ToolCallIcon.Terminal, "bash", null, null);

    command = command.Replace("\r\n", "; ", StringComparison.Ordinal).Replace('\r', ';').Replace('\n', ';');
    List<Segment> segments = SplitSegments(command);
    int firstSegment = SkipLeadingDirectoryChanges(command, segments);
    if (firstSegment >= segments.Count)
      return ToolCallLabels.CreateLabel(ToolCallIcon.Terminal, "bash", truncated ? "…" : null, null);

    Segment segment = segments[firstSegment];
    List<ShellWord> words = ReadWords(command, segment);
    int commandWord = FindCommandWord(words);
    if (commandWord < 0)
      return ToolCallLabels.CreateLabel(ToolCallIcon.Terminal, "bash", truncated ? "…" : null, null);

    string title = words[commandWord].Value;
    string remainder = command[words[commandWord].End..].Trim();
    if (truncated)
      remainder += "…";
    return ToolCallLabels.CreateLabel(ToolCallIcon.Terminal, title, remainder, null);
  }

  private static bool TryGetCommand(JsonElement arguments, out string command, out bool truncated) {
    command = "";
    truncated = false;
    if (!ToolCallExcerpt.TryGetProperty(arguments, "command", out JsonElement value))
      return false;
    if (value.ValueKind == JsonValueKind.String) {
      command = value.GetString()!;
      return true;
    }
    if (ToolCallExcerpt.IsTruncated(value) && value.TryGetProperty("$truncated", out JsonElement prefix) &&
        prefix.ValueKind == JsonValueKind.String) {
      command = prefix.GetString()!;
      truncated = true;
      return true;
    }
    return false;
  }

  private static List<Segment> SplitSegments(string command) {
    var segments = new List<Segment>();
    int start = 0;
    char quote = '\0';
    bool escaped = false;
    int index = 0;
    while (index < command.Length) {
      char character = command[index];
      if (escaped) {
        escaped = false;
        index++;
        continue;
      }
      if (character == '\\' && quote != '\'') {
        escaped = true;
        index++;
        continue;
      }
      if (quote != '\0') {
        if (character == quote)
          quote = '\0';
        index++;
        continue;
      }
      if (character is '\'' or '"' or '`') {
        quote = character;
        index++;
        continue;
      }
      if (character == ';' || index + 1 < command.Length &&
          (command.AsSpan(index, 2).SequenceEqual("&&") || command.AsSpan(index, 2).SequenceEqual("||"))) {
        segments.Add(new Segment(start, index));
        if (character != ';')
          index++;
        start = index + 1;
      }
      index++;
    }
    segments.Add(new Segment(start, command.Length));
    return segments;
  }

  private static int SkipLeadingDirectoryChanges(string command, List<Segment> segments) {
    for (int index = 0; index < segments.Count; index++) {
      List<ShellWord> words = ReadWords(command, segments[index]);
      int commandWord = SkipAssignments(words, 0);
      if (commandWord >= words.Count)
        continue;
      if (!words[commandWord].Value.Equals("cd", StringComparison.OrdinalIgnoreCase) ||
          commandWord + 1 >= words.Count || index + 1 == segments.Count)
        return index;
    }
    return segments.Count;
  }

  private static List<ShellWord> ReadWords(string command, Segment segment) {
    var words = new List<ShellWord>();
    int index = segment.Start;
    while (index < segment.End) {
      while (index < segment.End && char.IsWhiteSpace(command[index]))
        index++;
      if (index == segment.End)
        break;

      var value = new StringBuilder();
      char quote = '\0';
      while (index < segment.End) {
        char character = command[index];
        if (character == '\\' && quote != '\'' && index + 1 < segment.End) {
          value.Append(command[index + 1]);
          index += 2;
          continue;
        }
        if (quote != '\0') {
          if (character == quote) {
            quote = '\0';
          } else {
            value.Append(character);
          }
          index++;
          continue;
        }
        if (character is '\'' or '"' or '`') {
          quote = character;
          index++;
          continue;
        }
        if (char.IsWhiteSpace(character))
          break;
        value.Append(character);
        index++;
      }
      words.Add(new ShellWord(value.ToString(), index));
    }
    return words;
  }

  private static int FindCommandWord(List<ShellWord> words) {
    int index = SkipAssignments(words, 0);
    while (index < words.Count) {
      string name = words[index].Value;
      if (name.Equals("proto", StringComparison.OrdinalIgnoreCase) && index + 1 < words.Count &&
          words[index + 1].Value.Equals("run", StringComparison.OrdinalIgnoreCase)) {
        index += 2;
      } else if (name.Equals("env", StringComparison.OrdinalIgnoreCase)) {
        index = SkipEnvOptions(words, index + 1);
      } else if (name.Equals("npx", StringComparison.OrdinalIgnoreCase)) {
        index = SkipNpxOptions(words, index + 1);
      } else if (name.Equals("sudo", StringComparison.OrdinalIgnoreCase)) {
        index = SkipOptionsWithValues(words, index + 1, ["-u", "--user", "-g", "--group", "-h", "--host", "-p", "--prompt", "-C", "--close-from", "-T", "--command-timeout", "-R", "--chroot", "-D", "--chdir"]);
      } else if (name.Equals("time", StringComparison.OrdinalIgnoreCase)) {
        index = SkipOptionsWithValues(words, index + 1, ["-o", "--output", "-f", "--format"]);
      } else {
        break;
      }
      index = SkipAssignments(words, index);
    }
    return index < words.Count ? index : -1;
  }

  private static int SkipEnvOptions(List<ShellWord> words, int index) {
    while (index < words.Count) {
      string value = words[index].Value;
      if (IsEnvironmentAssignment(value)) {
        index++;
      } else if (value is "--" or "-i" or "--ignore-environment" or "-0" or "--null" or "-v" or "--debug") {
        index++;
      } else if (value is "-u" or "--unset" or "-C" or "--chdir") {
        index += Math.Min(2, words.Count - index);
      } else if (value.StartsWith("--unset=", StringComparison.Ordinal) || value.StartsWith("--chdir=", StringComparison.Ordinal)) {
        index++;
      } else {
        break;
      }
    }
    return index;
  }

  private static int SkipNpxOptions(List<ShellWord> words, int index) {
    while (index < words.Count && words[index].Value.StartsWith('-')) {
      string value = words[index].Value;
      if (value == "--")
        return index + 1;
      if (value is "--package" or "-p" or "--call" or "-c")
        index += Math.Min(2, words.Count - index);
      else
        index++;
    }
    return index;
  }

  private static int SkipOptionsWithValues(List<ShellWord> words, int index, string[] optionsWithValues) {
    while (index < words.Count) {
      string value = words[index].Value;
      if (value == "--")
        return index + 1;
      if (!value.StartsWith('-'))
        break;
      if (optionsWithValues.Contains(value, StringComparer.Ordinal))
        index += Math.Min(2, words.Count - index);
      else
        index++;
    }
    return index;
  }

  private static int SkipAssignments(List<ShellWord> words, int index) {
    while (index < words.Count && IsEnvironmentAssignment(words[index].Value))
      index++;
    return index;
  }

  private static bool IsEnvironmentAssignment(string word) {
    int equals = word.IndexOf('=');
    if (equals <= 0 || !(char.IsAsciiLetter(word[0]) || word[0] == '_'))
      return false;
    for (int index = 1; index < equals; index++)
      if (!(char.IsAsciiLetterOrDigit(word[index]) || word[index] == '_'))
        return false;
    return true;
  }
}
