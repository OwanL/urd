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
      // Playwright can quote the entire node key when an accessible name contains a colon.
      // Read past the quote for role validation, but preserve the original text for emission.
      ReadOnlySpan<char> key = content.AsSpan();
      if (key[^1] == ':')
        key = key[..^1];
      if (key.Length > 2 && key[0] is '\'' or '"' && key[^1] == key[0])
        key = key[1..^1];
      int roleEnd = key.IndexOfAny(' ', ':');
      string role = (roleEnd < 0 ? key : key[..roleEnd]).ToString();
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

  // Renders one uniform depth cut of the tree: every node down to one depth, whole, with no
  // sibling collapsing or line clipping, so kept refs stay valid and deeper descendants remain
  // in the saved full view. Chooses the deepest depth that fits the given budget and returns
  // null when the capture does not parse, no depth fits, or no omitted subtree can be expanded
  // from a kept ref, leaving truncation to the shared cap.
  internal static string? DepthView(string snapshot, long maxBytes, int maxLines) {
    if (maxLines < 1 || maxBytes < 1 || !TryParse(snapshot, out Node root))
      return null;
    List<long> depthBytes = [];
    List<int> depthLines = [];
    Measure(root, depthBytes, depthLines);
    // A node's measured bytes include its line break, which the final emission drops.
    long lines = 0;
    long bytes = -1;
    int depth = 0;
    while (depth < depthLines.Count) {
      long nextLines = lines + depthLines[depth];
      long nextBytes = bytes + depthBytes[depth];
      if (nextLines > maxLines || nextBytes > maxBytes)
        break;
      lines = nextLines;
      bytes = nextBytes;
      depth++;
    }
    if (depth == 0 || (depth < depthLines.Count && !HasExpandableCut(root, depth - 1, false)))
      return null;
    return EmitDepth(root, depth - 1);
  }

  private static bool HasExpandableCut(Node node, int depth, bool hasRef) {
    hasRef |= node.Reference is not null;
    if (node.Depth == depth)
      return hasRef && node.Children.Count > 0;
    return node.Children.Any(child => HasExpandableCut(child, depth, hasRef));
  }

  // Accumulates each node's line and byte weight into its depth bucket.
  private static void Measure(Node node, List<long> depthBytes, List<int> depthLines) {
    foreach (Node child in node.Children) {
      while (depthLines.Count <= child.Depth) {
        depthLines.Add(0);
        depthBytes.Add(0);
      }
      depthLines[child.Depth]++;
      depthBytes[child.Depth] += (long)Encoding.UTF8.GetByteCount(child.Line) + 2 * child.Depth + 3;
      Measure(child, depthBytes, depthLines);
    }
  }

  private static string EmitDepth(Node root, int depth) {
    List<string> lines = [];
    foreach (Node child in root.Children)
      EmitDepth(child, depth, lines);
    return string.Join('\n', lines);
  }

  private static void EmitDepth(Node node, int depth, List<string> lines) {
    lines.Add(new string(' ', node.Depth * 2) + "- " + node.Line);
    if (node.Depth < depth)
      foreach (Node child in node.Children)
        EmitDepth(child, depth, lines);
  }

  // Parsed once and never mutated afterwards; views only read it.
  private sealed class Node(string line, string? reference, int depth) {
    public string Line { get; } = line;
    public string? Reference { get; } = reference;
    public int Depth { get; } = depth;
    public List<Node> Children { get; } = [];
  }
}
