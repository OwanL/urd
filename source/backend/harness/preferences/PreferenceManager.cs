using Urd.Core.Providers;
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

  public void SetLanHostingEnabled(bool enabled) =>
      store.Preferences.Update(preferences => preferences with { LanHostingEnabled = enabled });

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

  public void SetBucketModels(ModelBucket bucket, IReadOnlyList<ConfiguredModel> models) {
    ArgumentNullException.ThrowIfNull(models);
    if (!Enum.IsDefined(bucket))
      throw new ArgumentOutOfRangeException(nameof(bucket));

    ConfiguredModel[] savedModels = models.ToArray();
    var seen = new HashSet<(Guid ConnectionId, string ModelId)>();
    foreach (ConfiguredModel model in savedModels) {
      ArgumentNullException.ThrowIfNull(model);
      model.Validate();
      if (!seen.Add((model.ConnectionId, model.ModelId)))
        throw new ArgumentException("Bucket provider/model pairs must be unique.", nameof(models));
    }

    store.Preferences.Update(preferences => preferences with {
      ModelChoices = preferences.ModelChoices.WithBucketModels(bucket, savedModels)
    });
  }

  internal BucketAssignment[] SuggestBuckets(ProviderModelCatalog catalog) =>
      Suggestions(store.Preferences.Read().ModelChoices, catalog);

  internal bool ConfirmBuckets(ProviderConnection connection, ProviderModelCatalog catalog,
      IReadOnlyList<BucketAssignment> assignments) {
    bool confirmed = false;
    store.Preferences.Update(preferences => {
      BucketAssignment[] current = Suggestions(preferences.ModelChoices, catalog);
      if (!current.SequenceEqual(assignments))
        return preferences;
      confirmed = true;
      if (assignments.Count == 0)
        return preferences;
      AuxiliaryModelPreferences choices = preferences.ModelChoices;
      foreach (BucketAssignment assignment in assignments)
        choices = choices.WithBucketModels(assignment.Bucket, [new(connection.Id, assignment.ModelId)]);
      return preferences with { ModelChoices = choices };
    });
    return confirmed;
  }

  private static BucketAssignment[] Suggestions(AuxiliaryModelPreferences choices, ProviderModelCatalog catalog) =>
      Enum.GetValues<ModelBucket>().Where(bucket => choices.ModelsFor(bucket).Count == 0)
          .Select(bucket => (Bucket: bucket, Model: BucketModelSuggestion.FindLatest(bucket, catalog)))
          .Where(item => item.Model is not null)
          .Select(item => new BucketAssignment(item.Bucket, item.Model!.Id)).ToArray();

  public void SetMaxSubagentDepth(int maxSubagentDepth) {
    ArgumentOutOfRangeException.ThrowIfNegative(maxSubagentDepth);
    store.Preferences.Update(preferences => preferences with { MaxSubagentDepth = maxSubagentDepth });
  }

  public void SetSubagentProviders(IReadOnlyList<Guid>? connectionIds) {
    Guid[]? savedIds = NormalizeSubagentProviders(connectionIds);
    store.Preferences.Update(preferences => preferences with { SubagentProviderConnectionIds = savedIds });
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

  public void SetSourceIgnores(SourceIgnoreRules rules) {
    ArgumentNullException.ThrowIfNull(rules);
    SourceIgnoreRules normalized = rules.Normalize();
    store.Preferences.Update(preferences => preferences with { SourceIgnores = normalized });
  }

  public void SetSourceIgnores(string category, string[] paths, string[] filenames, string[]? includes = null) {
    ArgumentNullException.ThrowIfNull(paths);
    ArgumentNullException.ThrowIfNull(filenames);
    if (category is not ("instructions" or "skills"))
      throw new ArgumentException("Source category must be 'instructions' or 'skills'.", nameof(category));

    SourceIgnoreRules normalized = new SourceIgnoreRules {
      Instructions = category == "instructions" ? paths : [],
      Skills = category == "skills" ? paths : [],
      InstructionIncludes = category == "instructions" ? includes ?? [] : [],
      SkillIncludes = category == "skills" ? includes ?? [] : [],
      Filenames = filenames
    }.Normalize();
    store.Preferences.Update(preferences => preferences with {
      SourceIgnores = category == "instructions"
          ? preferences.SourceIgnores with { Instructions = normalized.Instructions,
              InstructionIncludes = normalized.InstructionIncludes, Filenames = normalized.Filenames }
          : preferences.SourceIgnores with { Skills = normalized.Skills, SkillIncludes = normalized.SkillIncludes }
    });
  }

  internal static Guid[]? NormalizeSubagentProviders(IReadOnlyList<Guid>? ids) {
    if (ids is null)
      return null;

    Guid[] savedIds = ids.ToArray();
    if (savedIds.Contains(Guid.Empty))
      throw new ArgumentException("Subagent provider connection IDs cannot be empty.", nameof(ids));
    if (savedIds.ToHashSet().Count != savedIds.Length)
      throw new ArgumentException("Subagent provider connection IDs must be unique.", nameof(ids));
    return savedIds;
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
