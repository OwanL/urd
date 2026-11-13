namespace Urd.Core.Context;

// Reads top-level scalar fields from a Markdown file's YAML frontmatter: plain or quoted
// values, and folded (>) or literal (|) blocks. Other YAML is ignored.
internal static class SkillFrontmatter {
  internal static Dictionary<string, string> Read(string file) {
    var fields = new Dictionary<string, string>(StringComparer.Ordinal);
    using var lines = File.ReadLines(file).GetEnumerator();
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
      if (value is "" or ">" or ">-" or "|" or "|-") {
        key = name;
        literal = value.StartsWith('|');
      } else {
        fields[name] = Unquote(value);
      }
    }

    if (key is not null)
      fields[key] = string.Join(literal ? "\n" : " ", block).Trim();
    return fields;
  }

  private static string Unquote(string value) =>
      value.Length >= 2 && (value[0] is '"' or '\'') && value[^1] == value[0] ? value[1..^1] : value;
}
