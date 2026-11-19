using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Urd.Core.Providers;

namespace Urd.Core.Usage;

public sealed class ModelPriceCatalog : IAsyncDisposable {
  private const int MaximumCacheBytes = 8 * 1024 * 1024;
  private static readonly TimeSpan FreshFor = TimeSpan.FromDays(1);
  private static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(15);
  private static readonly Lazy<ModelPriceSnapshot> Seed = new(ReadBundled);
  private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) {
    RespectRequiredConstructorParameters = true
  };

  private readonly object gate = new();
  private readonly string cachePath;
  private readonly HttpClient client;
  private readonly TimeProvider clock;
  private readonly ModelsDevPriceSource source;
  private readonly CancellationTokenSource stop = new();
  private ModelPriceSnapshot current;
  private DateTimeOffset mayRefreshAt;
  private Task? refresh;
  private Task? shutdown;

  public static ModelPriceCatalog Open(string dataRoot) => new(Path.Combine(dataRoot, "model-prices.json"),
      new SocketsHttpHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.All }, TimeProvider.System);

  internal ModelPriceCatalog(string cachePath, HttpMessageHandler handler, TimeProvider clock) {
    this.cachePath = cachePath;
    this.clock = clock;
    DateTimeOffset now = clock.GetUtcNow();
    ModelPriceSnapshot? disk = ReadCache(cachePath, now);
    current = disk ?? Bundled;
    mayRefreshAt = disk?.Data.CapturedAt.Add(RetryAfter) ?? now;
    client = new(handler) { Timeout = TimeSpan.FromSeconds(20), MaxResponseContentBufferSize = ModelsDevPriceSource.MaximumFeedBytes };
    source = new(client);
  }

  internal static ModelPriceSnapshot Bundled => Seed.Value;

  internal UsageRates? FindRates(ProviderConnection connection, string model, bool allowRefresh = true) {
    ModelPriceSnapshot snapshot = Volatile.Read(ref current);
    UsageRates? rate = snapshot.FindRates(connection, model);
    if (allowRefresh && (rate is null || clock.GetUtcNow() - snapshot.Data.CapturedAt >= FreshFor))
      _ = RefreshAsync();
    return rate;
  }

  internal Task RefreshAsync() {
    lock (gate) {
      if (shutdown is not null) return Task.CompletedTask;
      if (refresh is { IsCompleted: false } || clock.GetUtcNow() < mayRefreshAt)
        return refresh ?? Task.CompletedTask;
      return refresh = Task.Run(ReloadAsync, CancellationToken.None);
    }
  }

  private async Task ReloadAsync() {
    try {
      CancellationToken cancellationToken = stop.Token;
      ModelPriceSnapshot next = await source.FetchAsync(clock, cancellationToken).ConfigureAwait(false);
      await SaveAsync(next.Data, cancellationToken).ConfigureAwait(false);
      Volatile.Write(ref current, next);
    } catch (OperationCanceledException) when (stop.IsCancellationRequested) {
      // The owner is shutting down, without publishing a partial refresh.
    } catch (Exception failure) when (IsCacheFailure(failure) || failure is HttpRequestException or OperationCanceledException) {
      Trace.TraceWarning("Model price refresh failed; using the last good catalog: {0}", failure.Message);
    } finally {
      lock (gate) mayRefreshAt = clock.GetUtcNow().Add(RetryAfter);
    }
  }

  private async Task SaveAsync(ModelPriceData data, CancellationToken cancellationToken) {
    byte[] contents = JsonSerializer.SerializeToUtf8Bytes(data, JsonOptions);
    if (contents.Length > MaximumCacheBytes) throw new InvalidDataException("Model price catalog exceeds the local cache limit.");
    string temporary = $"{cachePath}.{Guid.NewGuid():N}.tmp";
    try {
      await File.WriteAllBytesAsync(temporary, contents, cancellationToken).ConfigureAwait(false);
      cancellationToken.ThrowIfCancellationRequested();
      File.Move(temporary, cachePath, overwrite: true);
    } finally {
      File.Delete(temporary);
    }
  }

  public ValueTask DisposeAsync() {
    lock (gate) return new(shutdown ??= StopAsync(refresh));
  }

  private async Task StopAsync(Task? pending) {
    using (stop)
    using (client) {
      await stop.CancelAsync().ConfigureAwait(false);
      if (pending is not null) await pending.ConfigureAwait(false);
    }
  }

  private static ModelPriceSnapshot? ReadCache(string path, DateTimeOffset now) {
    try {
      using FileStream file = File.OpenRead(path);
      if (file.Length > MaximumCacheBytes) return null;
      ModelPriceData? data = JsonSerializer.Deserialize<ModelPriceData>(file, JsonOptions);
      return data is not null && data.CapturedAt <= now && data.CapturedAt >= Bundled.Data.CapturedAt ? new(data) : null;
    } catch (Exception failure) when (IsCacheFailure(failure)) {
      return null;
    }
  }

  private static ModelPriceSnapshot ReadBundled() {
    using Stream stream = typeof(ModelPriceCatalog).Assembly.GetManifestResourceStream("Urd.ModelPriceSeed.json")
        ?? throw new InvalidOperationException("The bundled model price seed is missing.");
    using var document = JsonDocument.Parse(stream);
    JsonElement root = document.RootElement;
    return ModelsDevPriceSource.Import(root.GetProperty("providers"), root.GetProperty("capturedAt").GetDateTimeOffset());
  }

  private static bool IsCacheFailure(Exception failure) => failure is IOException or InvalidDataException or JsonException or UnauthorizedAccessException;
}
