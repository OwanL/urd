using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text.Json;

namespace Urd.Core.Providers;

// Owns a socket and its continuation for one sequential agent run.
internal sealed class ChatGptSocketRun : IDisposable {
  internal const string PropertyName = "urd.chatgpt.socket-run";
  private static readonly TimeSpan handshakeTimeout = TimeSpan.FromSeconds(10);
  private const int MaximumResponseIds = 1024;
  private readonly ChatGptContinuation continuation = new();
  private readonly HashSet<string> responseIds = new(StringComparer.Ordinal);
  private WebSocket? socket;
  private (Guid Session, Guid Connection, long Generation, long CredentialVersion)? scope;
  private bool httpOnly;
  private bool completed;
  private bool turnOpen;
  private bool disposed;

  internal static async Task<WebSocket> ConnectAsync(string accessToken, Guid sessionId, CancellationToken ct) {
    var client = new ClientWebSocket();
    var invoker = new HttpMessageInvoker(new SocketsHttpHandler { AllowAutoRedirect = false });
    try {
      client.Options.SetRequestHeader("Authorization", $"Bearer {accessToken}");
      client.Options.SetRequestHeader("session-id", sessionId.ToString("D"));
#pragma warning disable S1075 // The ChatGPT WebSocket endpoint is fixed, not user-configurable.
      await client.ConnectAsync(new Uri("wss://api.openai.com/v1/responses"), invoker, ct).ConfigureAwait(false);
#pragma warning restore S1075
      return new OwnedSocket(client, invoker);
    } catch {
      client.Dispose();
      invoker.Dispose();
      throw;
    }
  }

  internal async Task<HttpClient?> OpenTurnAsync(ProviderState state, Guid sessionId,
      Func<string, Guid, CancellationToken, Task<WebSocket>> connect, CancellationToken ct,
      Action<Exception>? handshakeFailed = null) {
    ObjectDisposedException.ThrowIf(disposed, this);
    if (turnOpen)
      throw new InvalidOperationException("A socket turn is already open.");
    ct.ThrowIfCancellationRequested();
    if (httpOnly)
      return null;

    var nextScope = (sessionId, state.Connection.Id, state.Generation, state.CredentialVersion);
    if (scope != nextScope || socket?.State != WebSocketState.Open || responseIds.Count >= MaximumResponseIds)
      Reset();
    if (socket is null) {
      string? token = state.Credentials?.AccessToken;
      if (string.IsNullOrWhiteSpace(token)) {
        httpOnly = true;
        return null;
      }
      using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
      timeout.CancelAfter(handshakeTimeout);
      Task<WebSocket>? connecting = null;
      bool obtained = false;
      try {
        connecting = connect(token, sessionId, timeout.Token);
        socket = await connecting.WaitAsync(timeout.Token).ConfigureAwait(false);
        obtained = true;
        ct.ThrowIfCancellationRequested();
        if (socket.State != WebSocketState.Open)
          throw new WebSocketException();
        scope = nextScope;
      } catch (Exception failure) {
        Reset();
        if (connecting is not null && !obtained)
          _ = DisposeLateSocketAsync(connecting);
        ct.ThrowIfCancellationRequested();
        handshakeFailed?.Invoke(failure);
        httpOnly = true;
        return null;
      }
    }
    completed = false;
    turnOpen = true;
    return new HttpClient(new TurnHandler(this), disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
  }

  internal void EndTurn(bool accepted) {
    if (!turnOpen)
      return;
    turnOpen = false;
    if (!accepted || !completed || socket?.State != WebSocketState.Open)
      Reset();
  }

  // Rebuilt history cannot use the old continuation. Preserve any handshake-driven HTTP fallback.
  internal void RetireConnection() {
    if (turnOpen)
      throw new InvalidOperationException("Cannot retire a socket during an open turn.");
    Reset();
  }

  public void Dispose() {
    if (disposed)
      return;
    disposed = true;
    turnOpen = false;
    Reset();
  }

  private void Reset() {
    socket?.Dispose();
    socket = null;
    scope = null;
    completed = false;
    continuation.Reset();
    responseIds.Clear();
  }

  private static async Task DisposeLateSocketAsync(Task<WebSocket> connecting) {
    try {
      (await connecting.ConfigureAwait(false)).Dispose();
    } catch {
      // An abandoned handshake cannot be used by this run.
    }
  }

  private sealed class TurnHandler(ChatGptSocketRun run) : HttpMessageHandler {
    private int sent;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
      if (Interlocked.Exchange(ref sent, 1) != 0 || !run.turnOpen || run.socket?.State != WebSocketState.Open) {
        run.Reset();
        throw new HttpRequestException("Responses WebSocket turn is unavailable.", new WebSocketException(WebSocketError.InvalidState));
      }
      try {
        if (request.Content is null)
          throw new InvalidOperationException();
        using var document = await JsonDocument.ParseAsync(await request.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        byte[] frame = run.continuation.CreateFrame(document.RootElement);
        await run.socket.SendAsync(frame.AsMemory(), WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
        var stream = new ResponsesSocketStream(run.socket, response => {
          run.continuation.Complete(response);
          run.completed = true;
        }, id => run.responseIds.Add(id));
        var content = new StreamContent(stream);
        content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content, RequestMessage = request };
      } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
        run.Reset();
        throw;
      } catch (Exception failure) {
        run.Reset();
        throw new HttpRequestException("Responses WebSocket request failed.", failure);
      }
    }
  }

  // The handshake invoker must outlive the upgraded connection.
  private sealed class OwnedSocket(ClientWebSocket socket, HttpMessageInvoker invoker) : WebSocket {
    public override WebSocketCloseStatus? CloseStatus => socket.CloseStatus;
    public override string? CloseStatusDescription => socket.CloseStatusDescription;
    public override WebSocketState State => socket.State;
    public override string? SubProtocol => socket.SubProtocol;
    public override void Abort() => socket.Abort();
    public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) =>
        socket.CloseAsync(closeStatus, statusDescription, cancellationToken);
    public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) =>
        socket.CloseOutputAsync(closeStatus, statusDescription, cancellationToken);
    public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) =>
        socket.ReceiveAsync(buffer, cancellationToken);
    public override ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken) =>
        socket.ReceiveAsync(buffer, cancellationToken);
    public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken) =>
        socket.SendAsync(buffer, messageType, endOfMessage, cancellationToken);
    public override ValueTask SendAsync(ReadOnlyMemory<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken) =>
        socket.SendAsync(buffer, messageType, endOfMessage, cancellationToken);
    public override void Dispose() {
      socket.Dispose();
      invoker.Dispose();
    }
  }
}
