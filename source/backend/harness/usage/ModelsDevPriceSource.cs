using System.Text.Json;

namespace Urd.Core.Usage;

internal sealed class ModelsDevPriceSource(HttpClient client) {
  internal static readonly Uri Feed = new("https://models.dev/experimental/v2.0/api.json?type=chat");
  internal const int MaximumFeedBytes = 32 * 1024 * 1024;

  internal async Task<ModelPriceSnapshot> FetchAsync(TimeProvider clock, CancellationToken cancellationToken) {
    using HttpResponseMessage response = await client.GetAsync(Feed, cancellationToken).ConfigureAwait(false);
    response.EnsureSuccessStatusCode();
    await using Stream content = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
    using var document = await JsonDocument.ParseAsync(content, cancellationToken: cancellationToken).ConfigureAwait(false);
    return Import(document.RootElement, clock.GetUtcNow(), cancellationToken);
  }

  internal static ModelPriceSnapshot Import(JsonElement document, DateTimeOffset capturedAt, CancellationToken cancellationToken = default) {
    if (document.ValueKind != JsonValueKind.Object || !RequiredProviders.All(provider =>
        Get(Get(document, provider), "models").ValueKind == JsonValueKind.Object))
      throw new InvalidDataException("Models.dev did not return the expected provider catalog.");

    Row[] rows = ReadRows(document, cancellationToken).ToArray();
    var linked = rows.Where(row => row.Canonical is not null).Select(row => row.Canonical!)
        .ToHashSet(StringComparer.Ordinal);
    var aliases = new List<ModelPriceAlias>();
    var rates = new List<UsageRates>();
    var covered = new HashSet<string>(StringComparer.Ordinal);
    foreach (var group in rows.Where(row => row.Canonical is not null || row.Provider == "openai" || linked.Contains(row.QualifiedId))
        .GroupBy(row => row.Canonical ?? row.QualifiedId, StringComparer.Ordinal)) {
      cancellationToken.ThrowIfCancellationRequested();
      string model = group.Key;
      int separator = model.IndexOf('/', StringComparison.Ordinal);
      string maker = model[..separator];
      string slug = model[(separator + 1)..];
      Row? direct = group.FirstOrDefault(row => row.Provider == maker && row.Id.Equals(slug, StringComparison.OrdinalIgnoreCase));
      UsageRates? rate = ReadRates(direct, model, capturedAt) ?? Benchmark(group, model, slug, capturedAt);
      if (rate is not null) rates.Add(rate);

      aliases.Add(new("", model, model, IsId: true));
      aliases.Add(new("", slug, model));
      if (direct is not null) aliases.Add(new("", direct.Name, model));
      foreach (Row row in group.Where(row => ModelPriceSnapshot.HasProviderScope(row.Provider))) {
        aliases.Add(new(row.Provider, row.Id, model, IsId: true));
        aliases.Add(new(row.Provider, row.Name, model));
        if (rate is not null) covered.Add(row.Provider);
      }
    }

    if (!RequiredProviders.All(covered.Contains))
      throw new InvalidDataException("Models.dev returned no usable rates for an expected provider catalog.");
    return new(new(ModelPriceData.CurrentVersion, capturedAt, [.. rates.OrderBy(rate => rate.Model, StringComparer.Ordinal)],
        [.. aliases.Distinct().OrderBy(alias => alias.Scope, StringComparer.Ordinal)
            .ThenBy(alias => alias.Label, StringComparer.Ordinal).ThenBy(alias => alias.Model, StringComparer.Ordinal)]));
  }

  private static readonly string[] RequiredProviders = ["openai", "github-copilot", "ollama-cloud"];

  private static IEnumerable<Row> ReadRows(JsonElement document, CancellationToken cancellationToken) {
    foreach (JsonProperty provider in document.EnumerateObject()) {
      cancellationToken.ThrowIfCancellationRequested();
      JsonElement models = Get(provider.Value, "models");
      if (models.ValueKind != JsonValueKind.Object) continue;
      foreach (JsonProperty model in models.EnumerateObject()) {
        if (Text(Get(model.Value, "type")) != "chat" || Text(Get(model.Value, "id")) != model.Name ||
            !ModelPriceSnapshot.ValidCanonicalId($"{provider.Name}/{model.Name}")) continue;
        string? name = Text(Get(model.Value, "name"));
        if (!ModelPriceSnapshot.ValidLabel(name)) continue;
        JsonElement canonical = Get(model.Value, "canonical_id");
        string? id = Text(canonical);
        if (canonical.ValueKind != JsonValueKind.Undefined && !ModelPriceSnapshot.ValidCanonicalId(id)) continue;
        yield return new(provider.Name, model.Name, name, id, Get(model.Value, "cost"));
      }
    }
  }

  private static UsageRates? Benchmark(IEnumerable<Row> rows, string model, string slug, DateTimeOffset capturedAt) {
    // OpenRouter's named paid listing is the benchmark, not a free/fast variant or the cheapest host.
    UsageRates? benchmark = SoleRate(rows.Where(row => row.Provider == "openrouter" &&
        row.Id[(row.Id.LastIndexOf('/') + 1)..].Equals(slug, StringComparison.OrdinalIgnoreCase)), model, capturedAt);
    if (benchmark is not null) return benchmark;
    // Mistral publishes flat hosted rates where some OpenRouter listings vary by time of day.
    benchmark = SoleRate(rows.Where(row => row.Provider == "mistral"), model, capturedAt);
    if (benchmark is not null) return benchmark;

    // Only use these hosts as a last resort when they provide a single unambiguous standard rate.
    return SoleRate(rows.Where(row => row.Provider is "github-copilot" or "ollama-cloud"), model, capturedAt);
  }

  private static UsageRates? SoleRate(IEnumerable<Row> rows, string model, DateTimeOffset capturedAt) {
    UsageRates[] quotes = rows.Select(row => ReadRates(row, model, capturedAt)).OfType<UsageRates>().Take(2).ToArray();
    return quotes.Length == 1 ? quotes[0] : null;
  }

  private static UsageRates? ReadRates(Row? row, string model, DateTimeOffset capturedAt) {
    if (row is null || !StandardMultipliers(Get(row.Cost, "multipliers"))) return null;
    JsonElement text = Get(row.Cost, "text");
    if (!TryReadPrice(Get(text, "input"), out TextPrice input) ||
        !TryReadPrice(Get(text, "output"), out TextPrice output) ||
        input.Base.Price == 0 && output.Base.Price == 0 ||
        input.Above is not null && output.Above is not null && input.Above != output.Above) return null;

    long? threshold = input.Above ?? output.Above;
    PriceBand longInput = input.Long ?? input.Base;
    PriceBand longOutput = output.Long ?? output.Base;
    string source = $"{Feed}#{row.QualifiedId}";
    return new(model, source, capturedAt, input.Base.Price, output.Base.Price, input.Base.Read,
        input.Base.Write30m, input.Base.Write12h, threshold, threshold is null ? null : longInput.Price,
        threshold is null ? null : longOutput.Price, threshold is null ? null : longInput.Read,
        threshold is null ? null : longInput.Write30m, threshold is null ? null : longInput.Write12h);
  }

  private static bool TryReadPrice(JsonElement element, out TextPrice price) {
    price = default;
    if (Amount(Get(element, "price")) is not decimal amount ||
        !ReadBand(element, amount, default, out PriceBand baseRate)) return false;
    price = new(baseRate);
    JsonElement overrides = Get(element, "overrides");
    if (overrides.ValueKind == JsonValueKind.Undefined) return true;
    if (overrides.ValueKind != JsonValueKind.Array) return false;
    foreach (JsonElement adjustment in overrides.EnumerateArray()) {
      JsonElement when = Get(adjustment, "when");
      if (IsAlternateServiceTier(when)) continue;
      JsonElement condition = Get(when, "context");
      if (price.Above is not null || !OnlyProperty(when, "context") || !Threshold(condition, out long threshold) ||
          !ReadBand(adjustment, baseRate.Price, baseRate, out PriceBand longRate)) return false;
      price = new(baseRate, threshold, longRate);
    }
    return true;
  }

  private static bool ReadBand(JsonElement element, decimal basePrice, PriceBand inherited, out PriceBand band) {
    band = default;
    JsonElement value = Get(element, "price");
    decimal? price = value.ValueKind == JsonValueKind.Undefined ? basePrice : Amount(value);
    JsonElement reasoning = Get(element, "reasoning");
    if (price is not decimal amount || reasoning.ValueKind != JsonValueKind.Undefined && Amount(reasoning) != amount)
      return false;
    JsonElement cache = Get(element, "cache");
    if (cache.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Object)) return false;
    JsonElement read = Get(cache, "read"), write = Get(cache, "write");
    band = new(amount, read.ValueKind == JsonValueKind.Undefined ? inherited.Read : Amount(read),
        WriteRate(write, "30m", inherited.Write30m), WriteRate(write, "12h", inherited.Write12h));
    return true;
  }

  private static decimal? WriteRate(JsonElement write, string ttl, decimal? inherited) =>
      write.ValueKind == JsonValueKind.Undefined ? inherited : Amount(Get(write, ttl));

  private static bool Threshold(JsonElement condition, out long threshold) {
    threshold = 0;
    if (OnlyProperty(condition, "gt")) return PositiveCount(Get(condition, "gt"), out threshold);
    if (!OnlyProperty(condition, "gte") || !PositiveCount(Get(condition, "gte"), out long from) || from <= 1) return false;
    threshold = from - 1; // UsagePricing stores the maximum input count in the base band.
    return true;
  }

  private static bool StandardMultipliers(JsonElement multipliers) => multipliers.ValueKind == JsonValueKind.Undefined ||
      multipliers.ValueKind == JsonValueKind.Array && multipliers.EnumerateArray().All(multiplier => {
        JsonElement when = Get(multiplier, "when");
        return IsAlternateServiceTier(when) || OnlyProperty(when, "region") || OnlyProperty(Get(when, "not"), "region") &&
            OnlyProperty(when, "not");
      });

  private static bool IsAlternateServiceTier(JsonElement when) => Text(Get(Get(when, "service_tier"), "equals")) is
      "priority" or "flex" or "fast" or "ultrafast" or "batch";

  private static bool PositiveCount(JsonElement value, out long count) {
    count = 0;
    return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out count) && count > 0;
  }

  private static bool OnlyProperty(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object &&
      element.TryGetProperty(name, out _) && element.EnumerateObject().Count() == 1;

  private static JsonElement Get(JsonElement element, string name) =>
      element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) ? value : default;

  private static string? Text(JsonElement element) => element.ValueKind == JsonValueKind.String ? element.GetString() : null;

  private static decimal? Amount(JsonElement element) => element.ValueKind == JsonValueKind.Number &&
      element.TryGetDecimal(out decimal price) && price >= 0 ? price : null;

  private sealed record Row(string Provider, string Id, string Name, string? Canonical, JsonElement Cost) {
    internal string QualifiedId => $"{Provider}/{Id}";
  }

  private readonly record struct PriceBand(decimal Price, decimal? Read, decimal? Write30m, decimal? Write12h);
  private readonly record struct TextPrice(PriceBand Base, long? Above = null, PriceBand? Long = null);
}
