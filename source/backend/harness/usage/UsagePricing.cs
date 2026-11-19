namespace Urd.Core.Usage;

public static class UsagePricing {
  public static long? NanoDollars(UsageRates? rates, UsageTokens tokens) {
    ArgumentNullException.ThrowIfNull(tokens);
    if (rates is null || !IsValid(rates) ||
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

  internal static bool IsValid(UsageRates rates) =>
      !string.IsNullOrWhiteSpace(rates.Model) && !string.IsNullOrWhiteSpace(rates.Source) && rates.CapturedAt != default &&
      rates.Input >= 0 && rates.Output >= 0 && !Negative(rates.CachedInput) &&
      !Negative(rates.CacheWrite30m) && !Negative(rates.CacheWrite12h) &&
      !Negative(rates.LongInput) && !Negative(rates.LongOutput) && !Negative(rates.LongCachedInput) &&
      !Negative(rates.LongCacheWrite30m) && !Negative(rates.LongCacheWrite12h) &&
      rates.LongContextThreshold is not <= 0;

  private static bool Negative(decimal? rate) => rate is < 0;

  private static bool AddCost(ref decimal total, long count, decimal? rate) {
    if (count == 0) return true;
    if (rate is not decimal price || price < 0) return false;
    total += price * count * 1_000m;
    return true;
  }
}
