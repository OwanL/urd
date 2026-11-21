namespace Urd.Core.Context;

// A loaded project instruction document with a directory scope and canonical file identity.
internal sealed class DiscoveredInstructionDocument {
  internal DiscoveredInstructionDocument(string text, string path, string scope) {
    Text = text ?? throw new ArgumentNullException(nameof(text));
    Path = System.IO.Path.GetFullPath(path);
    Scope = System.IO.Path.GetFullPath(scope);
  }

  internal string Text { get; }
  internal string Path { get; }
  internal string Scope { get; }
  internal string Identity => Path;

  internal int ScopeDepth => Scope
      .Split([System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries)
      .Length;

  internal bool AppliesTo(string directory) => IsWithin(directory, Scope);

  internal string GetText() => $"""
      The following project instructions apply only to files under directory: {Scope}
      Source file: {Path}

      {Text}
      """;

  private static bool IsWithin(string directory, string scope) {
    string relative = System.IO.Path.GetRelativePath(scope, System.IO.Path.GetFullPath(directory));
    return relative == "." ||
        (!System.IO.Path.IsPathRooted(relative) && relative != ".." &&
         !relative.StartsWith($"..{System.IO.Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
         !relative.StartsWith($"..{System.IO.Path.AltDirectorySeparatorChar}", StringComparison.Ordinal));
  }
}
