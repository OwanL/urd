using Urd.Core.Storage;

namespace Urd.Core.Preferences;

// Validates and persists application preferences without owning the shared store.
public sealed class PreferenceManager {
  private readonly UrdStore store;

  public PreferenceManager(UrdStore store) {
    ArgumentNullException.ThrowIfNull(store);
    this.store = store;
  }

  public UserPreferences Read(string? fallbackWorkingDirectory = null) {
    UserPreferences persisted = store.Preferences.Read();
    string? workingDirectory = persisted.WorkingDirectory
        ?? fallbackWorkingDirectory ?? Path.GetPathRoot(Environment.CurrentDirectory);
    return persisted.WorkingDirectory is not null ? persisted : persisted with { WorkingDirectory = workingDirectory };
  }

  public void SetWorkingDirectory(string? path) {
    string? normalized = path is null ? null : NormalizeWorkingDirectory(path);
    store.Preferences.Update(preferences => preferences with { WorkingDirectory = normalized });
  }

  public void SetCompactionTriggerTokenCount(int? compactionTriggerTokenCount) {
    if (compactionTriggerTokenCount is <= 0)
      throw new ArgumentOutOfRangeException(nameof(compactionTriggerTokenCount),
          "Compaction trigger token count must be a positive integer or null.");

    store.Preferences.Update(preferences =>
        preferences with { CompactionTriggerTokenCount = compactionTriggerTokenCount });
  }

  internal static string NormalizeWorkingDirectory(string path) {
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

    return normalized;
  }

  private static ArgumentException InvalidWorkingDirectory() =>
      new("Working directory must be an existing, fully qualified directory.");
}
