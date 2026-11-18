using Urd.Core.Preferences;

namespace Urd.Core.Providers;

// Resolves explicit auxiliary choices and adds catalog-derived candidates for subagents only.
public sealed class ModelSelector {
  private readonly ProviderManager providers;

  public ModelSelector(ProviderManager providers) {
    ArgumentNullException.ThrowIfNull(providers);
    this.providers = providers;
  }

  public Task<LlmSelection> ResolveAsync(
      AuxiliaryModelPurpose purpose, UserPreferences preferences, CancellationToken cancellationToken = default) =>
      ResolveAsync(purpose, preferences, null, cancellationToken);

  internal async Task<LlmSelection> ResolveAsync(AuxiliaryModelPurpose purpose, UserPreferences preferences,
      IReadOnlyList<Guid>? connectionIds, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(preferences);
    ModelChoice choice = preferences.ModelChoices.ChoiceFor(purpose);
    choice.Validate();

    if (choice.ConfiguredModel() is not { } configured)
      return await ResolveBucketAsync(choice.Bucket!.Value, preferences, connectionIds, false, cancellationToken);
    if (connectionIds?.Contains(configured.ConnectionId) == false)
      throw new AuxiliaryModelUnavailableException("The configured model provider is not enabled for this subagent.");
    return await ResolveConfiguredAsync(configured, cancellationToken);
  }

  public async Task<LlmSelection> ResolveAsync(
      string bucket, UserPreferences preferences, CancellationToken cancellationToken = default) =>
      await ResolveAsync(ParseBucket(bucket), preferences, cancellationToken);

  public Task<LlmSelection> ResolveAsync(
      ModelBucket bucket, UserPreferences preferences, CancellationToken cancellationToken = default) =>
      ResolveBucketAsync(bucket, preferences, null, false, cancellationToken);

  public async Task<LlmSelection> ResolveSubagentAsync(string bucket, UserPreferences preferences,
      IReadOnlyList<Guid>? connectionIds, CancellationToken cancellationToken = default) =>
      await ResolveBucketAsync(ParseBucket(bucket), preferences, connectionIds, true, cancellationToken);

  internal Task<bool> HasSubagentProvidersAsync(IReadOnlyList<Guid>? connectionIds,
      CancellationToken cancellationToken = default) => providers.HasSelectedProvidersAsync(connectionIds, cancellationToken);

  private async Task<LlmSelection> ResolveBucketAsync(ModelBucket bucket, UserPreferences preferences,
      IReadOnlyList<Guid>? connectionIds, bool useFallback, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(preferences);
    IReadOnlyList<ConfiguredModel> assignments = preferences.ModelChoices.ModelsFor(bucket);
    if (!useFallback && assignments.Count == 0)
      throw new AuxiliaryModelUnavailableException($"No model is configured for the {BucketName(bucket)} bucket.");

    ILookup<Guid, string> modelsByConnection = assignments.ToLookup(model => model.ConnectionId, model => model.ModelId);
    IReadOnlyList<Guid>? requestedConnections = useFallback ? connectionIds : modelsByConnection
        .Select(group => group.Key).Where(id => connectionIds?.Contains(id) != false).ToArray();
    var catalogs = await providers.ListAvailableModelCatalogsAsync(requestedConnections, cancellationToken);
    var candidates = new List<(ProviderConnection Connection, ProviderModel Model)>();
    foreach (var (connection, catalog) in catalogs) {
      int previousCount = candidates.Count;
      foreach (string modelId in modelsByConnection[connection.Id]) {
        if (FindModel(catalog, modelId) is { } model)
          candidates.Add((connection, model));
      }
      if (useFallback && candidates.Count == previousCount && SubagentModelFallback.FindLatest(bucket, catalog) is { } fallback)
        candidates.Add((connection, fallback));
    }

    if (candidates.Count == 0) {
      string name = BucketName(bucket);
      throw new AuxiliaryModelUnavailableException(useFallback
          ? $"No enabled provider has a configured or automatic {name} model. Connect an enabled provider, refresh its catalog, or assign a model in Settings → Model buckets."
          : $"No model is available for the {name} bucket.");
    }

    var (chosenConnection, chosenModel) = candidates[Random.Shared.Next(candidates.Count)];
    return await BuildSelectionAsync(chosenConnection, chosenModel, cancellationToken);
  }

  private async Task<LlmSelection> ResolveConfiguredAsync(ConfiguredModel configured, CancellationToken cancellationToken) {
    ProviderConnection connection;
    ProviderModelCatalog catalog;
    try {
      connection = await providers.GetAsync(configured.ConnectionId, cancellationToken);
      catalog = await providers.GetModelCatalogAsync(connection.Id, cancellationToken);
    } catch (KeyNotFoundException failure) {
      throw ProviderUnavailable(failure);
    }

    ProviderModel model = FindModel(catalog, configured.ModelId)
        ?? throw new AuxiliaryModelUnavailableException("The configured model is unavailable; select a different model.");
    return await BuildSelectionAsync(connection, model, cancellationToken);
  }

  private async Task<LlmSelection> BuildSelectionAsync(ProviderConnection connection, ProviderModel model,
      CancellationToken cancellationToken) {
    IReadOnlyList<ModelSettings> settings;
    try {
      settings = await providers.GetModelSettingsAsync(connection.Id, cancellationToken);
    } catch (KeyNotFoundException failure) {
      throw ProviderUnavailable(failure);
    }

    ModelSettings? modelSettings = settings.FirstOrDefault(candidate =>
        string.Equals(candidate.ModelId, model.Id, StringComparison.Ordinal));
    return new LlmSelection(connection, model.Id, "high", modelSettings?.Family ?? ModelFamily.Other,
        modelSettings?.SupportsImages ?? false, modelSettings?.ContextWindow ?? model.ContextWindow);
  }

  private static ProviderModel? FindModel(ProviderModelCatalog catalog, string modelId) =>
      catalog.Models.FirstOrDefault(candidate => string.Equals(candidate.Id, modelId, StringComparison.Ordinal));

  private static AuxiliaryModelUnavailableException ProviderUnavailable(KeyNotFoundException failure) =>
      new("The configured model provider is unavailable; select a different model.", failure);

  private static ModelBucket ParseBucket(string bucket) => bucket switch {
    "small" => ModelBucket.Small,
    "medium" => ModelBucket.Medium,
    "large" => ModelBucket.Large,
    _ => throw new ArgumentException($"Unknown model bucket '{bucket}'. Expected small, medium, or large.", nameof(bucket))
  };

  private static string BucketName(ModelBucket bucket) => bucket.ToString().ToLowerInvariant();
}
