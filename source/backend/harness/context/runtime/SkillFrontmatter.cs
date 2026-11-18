namespace Urd.Core.Context;

// Reads top-level scalar fields from a Markdown file's YAML frontmatter: plain or quoted
// values, and folded (>) or literal (|) blocks. Trailing comments are dropped. Other YAML
// is ignored.
internal static class SkillFrontmatter {
  internal static Dictionary<string, string> Read(string file) {
    using var reader = File.OpenText(file);
    return Read(reader);
  }

  internal static Dictionary<string, string> Read(TextReader reader) {
    var fields = new Dictionary<string, string>(StringComparer.Ordinal);
    IEnumerable<string> ReadLines() {
      while (reader.ReadLine() is { } line)
        yield return line;
    }
    using var lines = ReadLines().GetEnumerator();
    if (!lines.MoveNext() || lines.Current.Trim() != "---")
      return fields;

    string? key = null;
    var block = new List<string>();
    bool literal = false;
    while (lines.MoveNext() && lines.Current.Trim() != "---") {
      string line = lines.Current;
      if (key is not null && (line.Length == 0 || char.IsWhiteSpace(line[0]))) {
        block.Add(line.Trim());
        continue;
      }

      if (key is not null)
        fields[key] = string.Join(literal ? "\n" : " ", block).Trim();
      key = null;
      block.Clear();

      int colon = line.IndexOf(':');
      if (colon <= 0 || char.IsWhiteSpace(line[0]))
        continue;

      string name = line[..colon].Trim();
      string value = line[(colon + 1)..].Trim();
      if (value.StartsWith('"') || value.StartsWith('\'')) {
        fields[name] = Quoted(value);
        continue;
      }

      value = WithoutComment(value);
      if (value is "" or ">" or ">-" or "|" or "|-") {
        key = name;
        literal = value.StartsWith('|');
      } else {
        fields[name] = value;
      }
    }

    if (key is not null)
      fields[key] = string.Join(literal ? "\n" : " ", block).Trim();
    return fields;
  }

  // A # starts a comment at the beginning of a value or after whitespace.
  private static string WithoutComment(string value) {
    for (int index = 0; index < value.Length; index++) {
      if (value[index] == '#' && (index == 0 || char.IsWhiteSpace(value[index - 1])))
        return value[..index].TrimEnd();
    }
    return value;
  }

  // Text up to the closing quote; '' escapes a single quote and \ escapes in double quotes.
  // An unterminated value is kept as written.
  private static string Quoted(string value) {
    char quote = value[0];
    var text = new System.Text.StringBuilder();
    int index = 1;
    while (index < value.Length) {
      char current = value[index];
      bool hasNext = index + 1 < value.Length;
      if (quote == '\'' && current == '\'' && hasNext && value[index + 1] == '\'') {
        text.Append('\'');
        index += 2;
      } else if (quote == '"' && current == '\\' && hasNext) {
        text.Append(value[index + 1]);
        index += 2;
      } else if (current == quote) {
        return text.ToString();
      } else {
        text.Append(current);
        index++;
      }
    }
    return value;
  }
}
