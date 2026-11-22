using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Urd.Core.Providers;

// Adapts one Responses WebSocket turn to the SSE stream consumed by the native SDK.
internal sealed class ResponsesSocketStream(WebSocket socket, Action<JsonElement> completed, Func<string, bool>? acceptId = null) : Stream {
  // A completed response can contain the full native output plus response metadata.
  internal const int MaximumMessageBytes = LlmProviderContext.MaximumBytes + 1024 * 1024;
  private readonly byte[] receiveBuffer = new byte[8192];
  private byte[] pending = [];
  private int pendingOffset;
  private string? responseId;
  private bool terminal;
  private bool failed;
  private ReceiveFailure? receiveFailure;
  private bool disposed;

  internal enum ReceiveFailure { PeerClosed, TransportRead, InvalidEvent }

  internal sealed class ReceiveException(ReceiveFailure failure, ProviderDiagnostic.Cause? transportCause = null) : IOException(failure switch {
    ReceiveFailure.PeerClosed => "Responses WebSocket peer closed the connection.",
    ReceiveFailure.TransportRead => "Responses WebSocket transport read failed.",
    _ => "Invalid or unexpected Responses WebSocket stream event."
  }) {
    internal ReceiveFailure Failure { get; } = failure;
    internal ProviderDiagnostic.Cause? TransportCause { get; } = transportCause;
  }

  public override bool CanRead => !disposed;
  public override bool CanSeek => false;
  public override bool CanWrite => false;
  public override long Length => throw new NotSupportedException();
  public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

  public override int Read(byte[] buffer, int offset, int count) =>
      ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

  public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
      ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

  public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) {
    ObjectDisposedException.ThrowIf(disposed, this);
    if (buffer.IsEmpty)
      return 0;
    if (failed)
      throw receiveFailure is { } failure ? new ReceiveException(failure) : new IOException("Invalid Responses WebSocket stream.");
    while (pendingOffset == pending.Length) {
      if (terminal)
        return 0;
      await ReceiveMessage(cancellationToken).ConfigureAwait(false);
    }
    int count = Math.Min(buffer.Length, pending.Length - pendingOffset);
    pending.AsMemory(pendingOffset, count).CopyTo(buffer);
    pendingOffset += count;
    return count;
  }

  private async Task ReceiveMessage(CancellationToken cancellationToken) {
    try {
      using var message = new MemoryStream();
      ValueWebSocketReceiveResult result;
      do {
        result = await ReceiveFrame(cancellationToken).ConfigureAwait(false);
        if (result.MessageType == WebSocketMessageType.Close)
          throw new ReceiveException(ReceiveFailure.PeerClosed);
        if (result.MessageType != WebSocketMessageType.Text || message.Length + result.Count > MaximumMessageBytes)
          throw new IOException();
        await message.WriteAsync(receiveBuffer.AsMemory(0, result.Count), cancellationToken).ConfigureAwait(false);
      } while (!result.EndOfMessage);

      using JsonDocument document = JsonDocument.Parse(message.GetBuffer().AsMemory(0, (int)message.Length));
      JsonElement root = document.RootElement;
      if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out JsonElement typeValue) ||
          typeValue.ValueKind != JsonValueKind.String)
        throw new IOException();
      string? type = typeValue.GetString();
      if (string.IsNullOrEmpty(type) || type.Any(character => char.IsControl(character) || char.IsWhiteSpace(character)))
        throw new IOException();
      bool hasResponse = root.TryGetProperty("response", out JsonElement response);
      if (hasResponse && response.ValueKind != JsonValueKind.Object)
        throw new IOException();
      string? eventId = ReadId(root, "response_id");
      string? objectId = hasResponse ? ReadId(response, "id") : null;
      // The plan endpoint can announce socket metadata before assigning a response ID.
      if (responseId is null && type == "codex.response.metadata" && !hasResponse && eventId is null)
        return;
      if (responseId is null) {
        if (type != "error") {
          if (type != "response.created" || objectId is null || (acceptId is not null && !acceptId(objectId)))
            throw new IOException();
          responseId = objectId;
        }
      } else if (type == "response.created") {
        throw new IOException();
      }
      if ((eventId is not null && eventId != responseId) || (objectId is not null && objectId != responseId))
        throw new IOException();
      if (type is "response.completed" or "response.incomplete" or "response.failed") {
        if (responseId is null || objectId != responseId)
          throw new IOException();
        if (type == "response.completed")
          completed(response.Clone());
      }
      terminal = type is "response.completed" or "response.incomplete" or "response.failed" or "error";
      // Reserializing eliminates literal newlines in the data field and produces valid SSE.
      byte[] json = JsonSerializer.SerializeToUtf8Bytes(root);
      byte[] prefix = Encoding.UTF8.GetBytes($"event: {type}\ndata: ");
      pending = new byte[prefix.Length + json.Length + 2];
      prefix.CopyTo(pending, 0);
      json.CopyTo(pending, prefix.Length);
      pending[^2] = (byte)'\n';
      pending[^1] = (byte)'\n';
      pendingOffset = 0;
    } catch (OperationCanceledException) {
      failed = true;
      throw;
    } catch (ReceiveException error) {
      failed = true;
      receiveFailure = error.Failure;
      throw;
    } catch (Exception error) when (error is WebSocketException or JsonException or IOException or InvalidOperationException or ArgumentException) {
      failed = true;
      // Provider payloads and close descriptions must not escape through exceptions.
      receiveFailure = ReceiveFailure.InvalidEvent;
      throw new ReceiveException(receiveFailure.Value, ProviderDiagnostic.Describe(error, null));
    }
  }

  private async ValueTask<ValueWebSocketReceiveResult> ReceiveFrame(CancellationToken cancellationToken) {
    try {
      return await socket.ReceiveAsync(receiveBuffer.AsMemory(), cancellationToken).ConfigureAwait(false);
    } catch (Exception error) when (error is WebSocketException or IOException or InvalidOperationException or ArgumentException) {
      throw new ReceiveException(ReceiveFailure.TransportRead, ProviderDiagnostic.Describe(error, null));
    }
  }

  private static string? ReadId(JsonElement element, string property) {
    if (!element.TryGetProperty(property, out JsonElement value))
      return null;
    if (value.ValueKind != JsonValueKind.String)
      throw new IOException();
    string? id = value.GetString();
    if (string.IsNullOrWhiteSpace(id) || id.Length > 1024)
      throw new IOException();
    return id;
  }

  protected override void Dispose(bool disposing) {
    disposed = true;
    base.Dispose(disposing);
  }

  public override void Flush() { }
  public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
  public override void SetLength(long value) => throw new NotSupportedException();
  public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
