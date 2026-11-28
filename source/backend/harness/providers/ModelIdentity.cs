namespace Urd.Core.Providers;

// Resolves provider catalog identity without turning transport or pricing metadata into model policy.
internal static class ModelIdentity {
  internal static ModelFamily Resolve(ProviderConnection connection, ProviderModel model) {
    if (model.Family is { } family && Enum.IsDefined(family))
      return family;
    if (connection.Route is ProviderRoute.OpenAI or ProviderRoute.ChatGpt)
      return ModelFamily.OpenAI;
    return Infer(model.Id, model.Name);
  }

  internal static ModelFamily Infer(string id, string? name = null, string? vendor = null) {
    ModelFamily? vendorFamily = VendorFamily(vendor);
    if (vendorFamily is not null)
      return vendorFamily.Value;

    foreach (string label in Labels(id, name)) {
      string normalized = label.Trim().ToLowerInvariant();
      string model = normalized[(normalized.LastIndexOf('/') + 1)..];
      if (normalized.StartsWith("anthropic/", StringComparison.Ordinal) || model.StartsWith("claude", StringComparison.Ordinal))
        return ModelFamily.Anthropic;
      if (normalized.StartsWith("google/", StringComparison.Ordinal) || model.StartsWith("gemini", StringComparison.Ordinal)
          || model.StartsWith("gemma", StringComparison.Ordinal))
        return ModelFamily.Google;
      if (normalized.StartsWith("openai/", StringComparison.Ordinal) || model.StartsWith("gpt-", StringComparison.Ordinal)
          || model.StartsWith("chatgpt-", StringComparison.Ordinal) || model.StartsWith("codex", StringComparison.Ordinal)
          || IsOpenAIReasoningModel(model))
        return ModelFamily.OpenAI;
    }
    return ModelFamily.Other;
  }

  private static ModelFamily? VendorFamily(string? vendor) => vendor?.Trim().ToLowerInvariant() switch {
    "openai" => ModelFamily.OpenAI,
    "anthropic" => ModelFamily.Anthropic,
    "google" or "google deepmind" => ModelFamily.Google,
    _ => null
  };

  private static IEnumerable<string> Labels(string id, string? name) {
    yield return id;
    if (!string.IsNullOrWhiteSpace(name) && !string.Equals(name, id, StringComparison.OrdinalIgnoreCase))
      yield return name;
  }

  private static bool IsOpenAIReasoningModel(string model) => model.Length >= 2 && model[0] == 'o'
      && char.IsAsciiDigit(model[1]) && (model.Length == 2 || model[2] is '-' or '.');
}
