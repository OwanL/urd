using Urd.Core.Storage;

namespace Urd.Core.Preferences;

// Validates and persists application preferences without owning the shared store.
public sealed class PreferenceManager {
  private readonly UrdStore store;

  public PreferenceManager(UrdStore store) {
    ArgumentNullException.ThrowIfNull(store);
    this.store = store;
  }

  public UserPreferences Read() => new(store.Preferences.ReadWorkingDirectory());

  public void SetWorkingDirectory(string? path) {
    if (path is null) {
      store.Preferences.SetWorkingDirectory(null);
      return;
    }

    if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
      throw InvalidWorkingDirectory();

    string normalized;
    try {
      normalized = Path.GetFullPath(path);
      string root = Path.GetPathRoot(normalized)!;
      while (normalized.Length > root.Length && Path.EndsInDirectorySeparator(normalized))
        normalized = Path.TrimEndingDirectorySeparator(normalized);
    } catch (Exception failure) when (failure is ArgumentException or NotSupportedException or PathTooLongException) {
      throw InvalidWorkingDirectory();
    }

    if (!Directory.Exists(normalized))
      throw InvalidWorkingDirectory();

    store.Preferences.SetWorkingDirectory(normalized);
  }

  private static ArgumentException InvalidWorkingDirectory() =>
      new("Working directory must be an existing, fully qualified directory.");
}
