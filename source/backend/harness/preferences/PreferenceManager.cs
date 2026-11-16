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

  public void SetModelChoice(AuxiliaryModelPurpose purpose, ModelChoice choice) {
    ArgumentNullException.ThrowIfNull(choice);
    if (!Enum.IsDefined(purpose))
      throw new ArgumentOutOfRangeException(nameof(purpose));
    choice.Validate();
    store.Preferences.Update(preferences => preferences with {
      ModelChoices = preferences.ModelChoices.WithChoice(purpose, choice)
    });
  }

  public void SetBucketModel(ModelBucket bucket, ConfiguredModel? model) {
    if (!Enum.IsDefined(bucket))
      throw new ArgumentOutOfRangeException(nameof(bucket));
    model?.Validate();
    store.Preferences.Update(preferences => preferences with {
      ModelChoices = preferences.ModelChoices.WithBucketModel(bucket, model)
    });
  }

  public void SetProtectedItems(ProtectedResourceCategory category, IReadOnlyList<string> items) {
    ArgumentNullException.ThrowIfNull(items);
    if (!Enum.IsDefined(category))
      throw new ArgumentOutOfRangeException(nameof(category));
    if (items.Any(item => string.IsNullOrWhiteSpace(item) || item.Any(char.IsControl)))
      throw new ArgumentException("Protected item identities must be nonempty and contain no control characters.", nameof(items));
    if (items.Distinct(StringComparer.Ordinal).Count() != items.Count)
      throw new ArgumentException("Protected item identities must be unique.", nameof(items));

    string[] savedItems = items.ToArray();
    store.Preferences.Update(preferences => preferences with {
      ProtectedResources = preferences.ProtectedResources.WithCategory(category, savedItems)
    });
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
