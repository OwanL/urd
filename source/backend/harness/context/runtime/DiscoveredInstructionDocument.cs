namespace Urd.Core.Context;

// A loaded project instruction document with a directory scope and canonical file identity.
internal sealed class DiscoveredInstructionDocument : StartingContextContribution {
  internal DiscoveredInstructionDocument(string text, string path, string scope) {
    Text = text ?? throw new ArgumentNullException(nameof(text));
    Path = System.IO.Path.GetFullPath(path);
    Scope = System.IO.Path.GetFullPath(scope);
  }

  internal string Text { get; }
  internal string Path { get; }
  internal string Scope { get; }
  internal string Identity => Path;

  public override int Order => Scope
      .Split([System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries)
      .Length;

  public override bool AppliesTo(StartingContextInput input) =>
      input.WorkingDirectory is not null && IsWithin(input.WorkingDirectory, Scope);

  public override string? GetText(StartingContextInput input) => $"""
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
