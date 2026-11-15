using Urd.Core.Providers;

namespace Urd.Core.Usage;

public static class UsagePricing {
  private static readonly Uri OfficialEndpoint = new("https://api.openai.com/v1/");
  private static readonly DateTimeOffset CapturedAt = new(2026, 11, 14, 0, 0, 0, TimeSpan.Zero);
  private const string Source = "https://developers.openai.com/api/docs/pricing";
  private const long LongContextThreshold = 272_000;
  private static readonly IReadOnlyDictionary<string, UsageRates> Catalog = CreateCatalog();

  public static UsageRates? FindRates(ProviderConnection connection, string model) {
    ArgumentNullException.ThrowIfNull(connection);
    ArgumentNullException.ThrowIfNull(model);
    if (connection.Route is not (ProviderRoute.OpenAI or ProviderRoute.ChatGpt) ||
        !connection.Endpoint.Equals(OfficialEndpoint)) return null;
    return Catalog.GetValueOrDefault(model);
  }

  public static long? NanoDollars(UsageRates? rates, UsageTokens tokens) {
    ArgumentNullException.ThrowIfNull(tokens);
    if (rates is null || string.IsNullOrWhiteSpace(rates.Model) || string.IsNullOrWhiteSpace(rates.Source) ||
        rates.CapturedAt == default || InvalidRates(rates) ||
        tokens.InputTokens is not long input || tokens.OutputTokens is not long output ||
        tokens.CachedInputTokens is not long cached || tokens.CacheWrite30mTokens is not long write30m ||
        tokens.CacheWrite12hTokens is not long write12h ||
        input < 0 || output < 0 || cached < 0 || write30m < 0 || write12h < 0 ||
        (decimal)cached + write30m + write12h > input) return null;

    long uncached = input - cached - write30m - write12h;
    bool longContext = rates.LongContextThreshold is long threshold && input > threshold;
    decimal? inputRate = longContext ? rates.LongInput : rates.Input;
    decimal? outputRate = longContext ? rates.LongOutput : rates.Output;
    decimal? cachedRate = longContext ? rates.LongCachedInput : rates.CachedInput;
    decimal? write30mRate = longContext ? rates.LongCacheWrite30m : rates.CacheWrite30m;
    decimal? write12hRate = longContext ? rates.LongCacheWrite12h : rates.CacheWrite12h;

    decimal total = 0;
    try {
      if (!AddCost(ref total, uncached, inputRate) ||
          !AddCost(ref total, cached, cachedRate) ||
          !AddCost(ref total, write30m, write30mRate) ||
          !AddCost(ref total, write12h, write12hRate) ||
          !AddCost(ref total, output, outputRate)) return null;
      decimal rounded = decimal.Round(total, 0, MidpointRounding.AwayFromZero);
      return rounded > long.MaxValue ? null : decimal.ToInt64(rounded);
    } catch (OverflowException) {
      return null;
    }
  }

  private static Dictionary<string, UsageRates> CreateCatalog() {
    var rates = new Dictionary<string, UsageRates>(StringComparer.Ordinal);
    Add("gpt-6-astra", 10m, 50m, 1m, write30m: 12.5m, write12h: 25m,
        longInput: 20m, longOutput: 75m, longCachedInput: 2m, longWrite30m: 25m, longWrite12h: 50m);
    Add("gpt-6.1-sol", 2m, 10m, .10m, write30m: 2.5m, write12h: 5m,
        longInput: 4m, longOutput: 15m, longCachedInput: .20m, longWrite30m: 5m, longWrite12h: 10m);
    Add("gpt-6.1-terra", 1m, 5m, .05m, write30m: 1.25m, write12h: 2.5m,
        longInput: 2m, longOutput: 7.5m, longCachedInput: .10m, longWrite30m: 2.5m, longWrite12h: 5m);
    Add("gpt-6-luna", .1m, .5m, .01m, write30m: .125m, write12h: .25m,
        longInput: .2m, longOutput: .75m, longCachedInput: .02m, longWrite30m: .25m, longWrite12h: .5m);
    Add("gpt-6-sol", 2m, 10m, .2m, write30m: 2.5m, write12h: 5m,
        longInput: 4m, longOutput: 15m, longCachedInput: .4m, longWrite30m: 5m, longWrite12h: 10m);
    Add("gpt-5.4", 2.5m, 15m, .25m, longInput: 5m, longOutput: 22.5m, longCachedInput: .5m);
    Add("gpt-5.5", 5m, 30m, .5m, longInput: 10m, longOutput: 45m, longCachedInput: 1m);
    Add("gpt-5.4-mini", .75m, 4.5m, .075m);
    Add("gpt-5.4-nano", .2m, 1.25m, .02m);
    Add("gpt-5.2", 1.75m, 14m, .175m);
    Add("gpt-5.1", 1.25m, 10m, .125m);
    Add("gpt-5", 1.25m, 10m, .125m);
    Add("gpt-5-mini", .25m, 2m, .025m);
    Add("gpt-5-nano", .05m, .4m, .005m);
    Add("gpt-4.1", 2m, 8m, .5m);
    Add("gpt-4.1-mini", .4m, 1.6m, .1m);
    Add("gpt-4o", 2.5m, 10m, 1.25m);
    Add("gpt-4o-mini", .15m, .6m, .075m);
    Add("o3", 2m, 8m, .5m);
    Add("gpt-5.3-codex", 1.75m, 14m, .175m);
    return rates;

    void Add(string model, decimal input, decimal output, decimal cachedInput,
        decimal? write30m = null, decimal? write12h = null,
        decimal? longInput = null, decimal? longOutput = null, decimal? longCachedInput = null,
        decimal? longWrite30m = null, decimal? longWrite12h = null) {
      rates.Add(model, new UsageRates(model, Source, CapturedAt, input, output, cachedInput,
          write30m, write12h, longInput is null ? null : LongContextThreshold,
          longInput, longOutput, longCachedInput, longWrite30m, longWrite12h));
    }
  }

  private static bool InvalidRates(UsageRates rates) =>
      rates.Input < 0 || rates.Output < 0 || Negative(rates.CachedInput) ||
      Negative(rates.CacheWrite30m) || Negative(rates.CacheWrite12h) ||
      Negative(rates.LongInput) || Negative(rates.LongOutput) || Negative(rates.LongCachedInput) ||
      Negative(rates.LongCacheWrite30m) || Negative(rates.LongCacheWrite12h) ||
      rates.LongContextThreshold is <= 0;

  private static bool Negative(decimal? rate) => rate is < 0;

  private static bool AddCost(ref decimal total, long count, decimal? rate) {
    if (count == 0) return true;
    if (rate is not decimal price || price < 0) return false;
    total += price * count * 1_000m;
    return true;
  }
}
