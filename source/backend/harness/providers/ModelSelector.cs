using Urd.Core.Preferences;

namespace Urd.Core.Providers;

// Resolves the user's exact bucket mapping or operation override without borrowing chat selection.
public sealed class ModelSelector {
  private readonly ProviderManager providers;

  public ModelSelector(ProviderManager providers) {
    ArgumentNullException.ThrowIfNull(providers);
    this.providers = providers;
  }

  public async Task<LlmSelection> ResolveAsync(
      AuxiliaryModelPurpose purpose, UserPreferences preferences, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(preferences);
    AuxiliaryModelPreferences choices = preferences.ModelChoices;
    ModelChoice choice = choices.ChoiceFor(purpose);
    choice.Validate();

    ConfiguredModel configured = choice.ConfiguredModel() ?? ResolveBucket(choices, choice.Bucket!.Value);
    return await ResolveConfiguredAsync(configured, cancellationToken);
  }

  public async Task<LlmSelection> ResolveAsync(
      string bucket, UserPreferences preferences, CancellationToken cancellationToken = default) {
    ModelBucket parsed = bucket switch {
      "small" => ModelBucket.Small,
      "medium" => ModelBucket.Medium,
      "large" => ModelBucket.Large,
      _ => throw new ArgumentException($"Unknown model bucket '{bucket}'. Expected small, medium, or large.", nameof(bucket))
    };
    return await ResolveAsync(parsed, preferences, cancellationToken);
  }

  public async Task<LlmSelection> ResolveAsync(
      ModelBucket bucket, UserPreferences preferences, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(preferences);
    return await ResolveConfiguredAsync(ResolveBucket(preferences.ModelChoices, bucket), cancellationToken);
  }

  private async Task<LlmSelection> ResolveConfiguredAsync(
      ConfiguredModel configured, CancellationToken cancellationToken) {
    configured.Validate();

    ProviderConnection connection;
    ProviderModelCatalog catalog;
    IReadOnlyList<ModelSettings> settings;
    try {
      connection = await providers.GetAsync(configured.ConnectionId, cancellationToken);
      catalog = await providers.GetModelCatalogAsync(connection.Id, cancellationToken);
      settings = await providers.GetModelSettingsAsync(connection.Id, cancellationToken);
    } catch (KeyNotFoundException failure) {
      throw new AuxiliaryModelUnavailableException(
          "The configured model provider is unavailable; select a different model.", failure);
    }

    ProviderModel? catalogModel = catalog.Models.FirstOrDefault(candidate =>
        string.Equals(candidate.Id, configured.ModelId, StringComparison.Ordinal));
    if (catalogModel is null)
      throw new AuxiliaryModelUnavailableException("The configured model is unavailable; select a different model.");

    ModelSettings? modelSettings = settings.FirstOrDefault(candidate =>
        string.Equals(candidate.ModelId, configured.ModelId, StringComparison.Ordinal));
    return new LlmSelection(connection, catalogModel.Id, "high", modelSettings?.Family ?? ModelFamily.Other,
        modelSettings?.SupportsImages ?? false, modelSettings?.ContextWindow ?? catalogModel.ContextWindow);
  }

  private static ConfiguredModel ResolveBucket(AuxiliaryModelPreferences choices, ModelBucket bucket) =>
      choices.ModelFor(bucket)
      ?? throw new AuxiliaryModelUnavailableException(
          $"No model is configured for the {bucket.ToString().ToLowerInvariant()} bucket.");
}
