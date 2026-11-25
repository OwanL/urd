using System.Text;
using System.Text.RegularExpressions;

namespace Urd.Core.Tools;

internal static class SnapshotTree {
  private const int MaxDepth = 128;
  private static readonly Regex RefAttribute = new(@"\[ref=((?:f\d+)?e\d+|f\d+)\]", RegexOptions.Compiled, TimeSpan.FromSeconds(2));

  // Unknown snapshot shapes are returned intact rather than silently dropping their content.
  internal static string? Changes(string before, string after) {
    if (string.Equals(before, after, StringComparison.Ordinal))
      return null;
    if (before.EndsWith('\n') != after.EndsWith('\n') || !TryParse(before, out Node oldRoot)
        || !TryParse(after, out Node newRoot))
      return after;

    HashSet<Node> changed = [];
    List<string> removals = [];
    if (!Mark(newRoot, oldRoot, changed, removals, false, out _))
      return after;
    List<string> lines = [];
    foreach (Node child in newRoot.Children)
      Emit(child, changed, lines);
    if (lines.Count == 0 && removals.Count == 0)
      return after;

    var result = new StringBuilder();
    if (lines.Count > 0)
      result.Append("Changed since the previous action:\n").Append(string.Join('\n', lines));
    if (removals.Count > 0) {
      if (result.Length > 0)
        result.Append('\n');
      result.Append("Removed: ").Append(string.Join("; ", removals));
    }
    return result.ToString();
  }

  private static bool TryParse(string snapshot, out Node root) {
    root = new Node("", null, -1);
    List<Node> chain = [root];
    string[] lines = snapshot.Split('\n');
    for (int index = 0; index < lines.Length; index++) {
      string line = lines[index];
      if (index == lines.Length - 1 && line.Length == 0 && snapshot.EndsWith('\n'))
        continue;
      int spaces = 0;
      while (spaces < line.Length && line[spaces] == ' ')
        spaces++;
      if (spaces % 2 != 0 || spaces + 2 >= line.Length || line[spaces] != '-' || line[spaces + 1] != ' '
          || line.Contains('\r') || spaces / 2 >= chain.Count || spaces / 2 >= MaxDepth)
        return false;
      string content = line[(spaces + 2)..];
      int roleEnd = content.IndexOfAny([' ', ':']);
      string role = roleEnd < 0 ? content : content[..roleEnd];
      if (role.Length == 0 || role.Any(c => !char.IsAsciiLetter(c) && c != '/'))
        return false;
      Match reference = RefAttribute.Match(content);
      var node = new Node(content, reference.Success ? reference.Groups[1].Value : null, spaces / 2);
      chain[node.Depth].Children.Add(node);
      if (chain.Count > node.Depth + 1)
        chain.RemoveRange(node.Depth + 1, chain.Count - node.Depth - 1);
      chain.Add(node);
    }
    return true;
  }

  private static bool Mark(Node current, Node baseline, HashSet<Node> changed, List<string> removals,
      bool emitAll, out bool any) {
    any = false;
    // Focus and other attributes can change on a container without changing its descendants.
    var oldChildren = new Dictionary<string, (Node Node, int Index)>(StringComparer.Ordinal);
    for (int i = 0; i < baseline.Children.Count; i++) {
      Node child = baseline.Children[i];
      if (!oldChildren.TryAdd(Key(child), (child, i)))
        return false;
    }

    var seen = new HashSet<string>(StringComparer.Ordinal);
    int previousIndex = -1;
    bool replaceChildren = false;
    foreach (Node child in current.Children) {
      string key = Key(child);
      if (!seen.Add(key))
        return false;
      if (oldChildren.TryGetValue(key, out var oldChild)) {
        if (oldChild.Index < previousIndex)
          replaceChildren = true;
        previousIndex = oldChild.Index;
      } else if (child.Reference is null) {
        replaceChildren = true;
      }
    }
    foreach (Node child in baseline.Children.Where(child => !seen.Contains(Key(child)))) {
      removals.Add(child.Line.TrimEnd(':'));
      replaceChildren |= child.Reference is null;
    }

    any = emitAll || !string.Equals(current.Line, baseline.Line, StringComparison.Ordinal);
    foreach (Node child in current.Children) {
      if (oldChildren.TryGetValue(Key(child), out var oldChild)) {
        if (!Mark(child, oldChild.Node, changed, removals, emitAll || replaceChildren, out bool childChanged))
          return false;
        any |= childChanged;
      } else {
        MarkSubtree(child, changed);
        any = true;
      }
    }
    if (any || replaceChildren)
      changed.Add(current);
    any |= replaceChildren;
    return true;
  }

  private static string Key(Node node) => node.Reference is null ? "line:" + node.Line : "ref:" + node.Reference;

  private static void MarkSubtree(Node node, HashSet<Node> changed) {
    changed.Add(node);
    foreach (Node child in node.Children)
      MarkSubtree(child, changed);
  }

  private static void Emit(Node node, HashSet<Node> changed, List<string> lines) {
    if (!changed.Contains(node))
      return;
    lines.Add(new string(' ', node.Depth * 2) + "- " + node.Line);
    foreach (Node child in node.Children)
      Emit(child, changed, lines);
  }

  private sealed class Node(string line, string? reference, int depth) {
    public string Line { get; } = line;
    public string? Reference { get; } = reference;
    public int Depth { get; } = depth;
    public List<Node> Children { get; } = [];
  }
}
