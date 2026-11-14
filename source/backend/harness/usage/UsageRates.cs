namespace Urd.Core.Usage;

public record UsageRates(
    string Model,
    string Source,
    DateTimeOffset CapturedAt,
    decimal Input,
    decimal Output,
    decimal? CachedInput,
    decimal? CacheWrite30m = null,
    decimal? CacheWrite12h = null,
    long? LongContextThreshold = null,
    decimal? LongInput = null,
    decimal? LongOutput = null,
    decimal? LongCachedInput = null,
    decimal? LongCacheWrite30m = null,
    decimal? LongCacheWrite12h = null);
