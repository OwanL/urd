using System.ClientModel;
using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenAI.Chat;

#pragma warning disable MEAI001, SCME0001 // Inspect the native chunk's choices instead of adapting it to output.

namespace Urd.Core.Providers;

internal static class ChatUsageTrailer {
  private static readonly TimeSpan readLimit = TimeSpan.FromSeconds(1);

  // The output limit is already terminal. Try just the next chunk, without waiting for DONE.
  internal static async ValueTask<UsageDetails?> ReadAsync<T>(IAsyncEnumerator<T> stream, Func<T, object?> nativeUpdate,
      ProviderCancellation stop, CancellationToken caller, TimeSpan? readTimeout = null) {
    caller.ThrowIfCancellationRequested();
    stop.SetDeadline(readTimeout is { } timeout && timeout != Timeout.InfiniteTimeSpan && timeout < readLimit
        ? timeout : readLimit);
    try {
      bool read = await stream.MoveNextAsync().ConfigureAwait(false);
      caller.ThrowIfCancellationRequested();
      if (!read || stop.IsCancellationRequested) return null;

      object? native = nativeUpdate(stream.Current);
      return IsUsageOnly(native) ? ProviderTokenUsage.Read(native) : null;
    } catch (Exception error) when (error is ClientResultException or HttpRequestException or IOException or
        OperationCanceledException or JsonException or FormatException or InvalidOperationException or
        KeyNotFoundException or ArgumentException or NotSupportedException) {
      caller.ThrowIfCancellationRequested();
      return null;
    } finally {
      stop.SetDeadline(Timeout.InfiniteTimeSpan);
    }
  }

  private static bool IsUsageOnly(object? update) {
    if (update is not StreamingChatCompletionUpdate chat ||
        !chat.Patch.TryGetJson("$.choices"u8, out ReadOnlyMemory<byte> choices))
      return false;

    var reader = new Utf8JsonReader(choices.Span);
    return reader.Read() && reader.TokenType == JsonTokenType.StartArray &&
        reader.Read() && reader.TokenType == JsonTokenType.EndArray;
  }
}

#pragma warning restore MEAI001, SCME0001
