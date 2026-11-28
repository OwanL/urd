using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using Urd.Core.Providers;

namespace Urd.Core.Usage;

internal sealed class ModelPriceSnapshot {
  private readonly FrozenDictionary<string, UsageRates> rates;
  private readonly FrozenDictionary<(string Scope, string Label), string?> exact;
  private readonly FrozenDictionary<(string Scope, string Label), string?> normalized;

  internal ModelPriceSnapshot(ModelPriceData data) {
    if (data.Version != ModelPriceData.CurrentVersion || data.CapturedAt == default ||
        data.Rates is not { Length: > 0 } || data.Aliases is not { Length: > 0 })
      throw new InvalidDataException("Invalid model price catalog.");

    var byModel = new Dictionary<string, UsageRates>(StringComparer.Ordinal);
    foreach (UsageRates rate in data.Rates) {
      if (rate is null || !ValidCanonicalId(rate.Model) || !UsagePricing.IsValid(rate) ||
          rate.CapturedAt != data.CapturedAt || !byModel.TryAdd(rate.Model, rate))
        throw new InvalidDataException("Invalid or duplicate model rate.");
    }

    var exactMatches = new Dictionary<(string, string), string?>();
    var normalizedMatches = new Dictionary<(string, string), string?>();
    foreach (ModelPriceAlias alias in data.Aliases) {
      if (alias is null || alias.Scope is null || alias.Scope.Length > 0 && !HasProviderScope(alias.Scope) ||
          !ValidLabel(alias.Label) || !ValidCanonicalId(alias.Model))
        throw new InvalidDataException("Invalid model price alias.");
      if (alias.IsId) Add(exactMatches, (alias.Scope, alias.Label), alias.Model);
      string key = Normalize(alias.Label);
      if (key.Length > 0) Add(normalizedMatches, (alias.Scope, key), alias.Model);
    }

    AddVerifiedAlias("openai", "gpt-6.2-sol", "openai/gpt-6.2-sol", "6.2sol");
    AddVerifiedAlias("github-copilot", "gpt-6.2-sol", "openai/gpt-6.2-sol", "6.2sol");
    // GitHub lists fast mode as a separate display choice, but it uses the same model.
    AddVerifiedAlias("github-copilot", "claude-opus-4.8", "anthropic/claude-opus-4-8", "Claude Opus 4.8 (fast mode) (preview)");

    void AddVerifiedAlias(string scope, string id, string canonical, string label) {
      if (exactMatches.GetValueOrDefault((scope, id)) == canonical)
        Add(normalizedMatches, (scope, Normalize(label)), canonical);
    }
    Data = data;
    rates = byModel.ToFrozenDictionary(StringComparer.Ordinal);
    exact = exactMatches.ToFrozenDictionary();
    normalized = normalizedMatches.ToFrozenDictionary();
  }

  internal ModelPriceData Data { get; }

  internal UsageRates? FindRates(ProviderConnection connection, string label) {
    if (!ValidLabel(label)) return null;
    string? scope = ProviderScope(connection);
    if (scope is not null && exact.TryGetValue((scope, label), out string? model))
      return RateFor(model);

    string key = Normalize(label);
    if (exact.TryGetValue(("", label), out model)) return RateFor(model);
    if (scope is not null && normalized.TryGetValue((scope, key), out model))
      return RateFor(model);
    return normalized.TryGetValue(("", key), out model) ? RateFor(model) : null;
  }

  internal static bool HasProviderScope(string provider) => provider is "openai" or "github-copilot" or "ollama-cloud" or "openrouter";

  internal static bool ValidCanonicalId(string? value) => ValidLabel(value) &&
      value.IndexOf('/') is > 0 && value.LastIndexOf('/') < value.Length - 1 &&
      value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '/' or ':');

  internal static bool ValidLabel([NotNullWhen(true)] string? value) =>
      !string.IsNullOrWhiteSpace(value) && value.Length <= 256 && !value.Any(char.IsControl);

  private static string? ProviderScope(ProviderConnection connection) {
    if (connection.Route == ProviderRoute.GitHubCopilot)
      return "github-copilot";
    Uri endpoint = connection.Endpoint;
    if (endpoint is not { IsAbsoluteUri: true, Scheme: "https", IsDefaultPort: true, UserInfo.Length: 0 } ||
        !string.IsNullOrEmpty(endpoint.Query) || !string.IsNullOrEmpty(endpoint.Fragment)) return null;
    return (endpoint.Host, endpoint.AbsolutePath.TrimEnd('/')) switch {
      ("api.openai.com", "/v1") => "openai",
      ("ollama.com", "/v1") => "ollama-cloud",
      ("api.githubcopilot.com", "" or "/v1") => "github-copilot",
      ("openrouter.ai", "/api/v1") => "openrouter",
      _ => null
    };
  }

  private static string Normalize(string label) {
    var result = new StringBuilder(label.Length);
    for (int i = 0; i < label.Length; i++) {
      char c = label[i];
      if (char.IsWhiteSpace(c)) continue;
      // A numeric hyphen, like a decimal point, can delimit a model version.
      if (c == '-') {
        ReadOnlySpan<char> next = label.AsSpan(i + 1).TrimStart();
        if (result.Length == 0 || !char.IsAsciiDigit(result[^1]) || next.IsEmpty || !char.IsAsciiDigit(next[0])) continue;
      }
      result.Append(char.ToLowerInvariant(c));
    }
    return result.ToString();
  }

  private UsageRates? RateFor(string? model) => model is null ? null : rates.GetValueOrDefault(model);

  private static void Add(Dictionary<(string, string), string?> index, (string, string) key, string model) {
    if (!index.TryAdd(key, model) && index[key] != model) index[key] = null;
  }
}

internal sealed record ModelPriceAlias(string Scope, string Label, string Model, bool IsId = false);

internal sealed record ModelPriceData(int Version, DateTimeOffset CapturedAt, UsageRates[] Rates, ModelPriceAlias[] Aliases) {
  internal const int CurrentVersion = 1;
}
