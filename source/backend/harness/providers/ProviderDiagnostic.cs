using System.Net.Sockets;
using System.Net.WebSockets;

namespace Urd.Core.Providers;

public enum ProviderDiagnosticTerminalKind {
  ResponseFailed,
  StandaloneError
}

// Safe to persist: no exception messages, URLs, credentials or request content.
public sealed record ProviderDiagnostic(ProviderRoute Route, Guid ConnectionId, Guid? SessionId, string Transport,
    string Stage, ProviderFailure Failure, int? HttpStatus, string? ExceptionType, string? HttpRequestError,
    string? SocketError, string? WebSocketError, string? WebSocketReceiveFailure) {
  public ProviderDiagnosticTerminalKind? TerminalKind { get; init; }

  internal sealed record Cause(int? HttpStatus, string? ExceptionType, string? HttpRequestError,
      string? SocketError, string? WebSocketError, string? WebSocketReceiveFailure);

  internal static Cause Describe(Exception error, int? status) {
    string? httpRequestError = null, socketError = null, webSocketError = null, webSocketReceiveFailure = null;
    string? exceptionType = null;
    for (Exception? cause = error; cause is not null; cause = cause.InnerException) {
      exceptionType = cause.GetType().Name;
      var requestError = cause switch {
        HttpRequestException http => http.HttpRequestError,
        HttpIOException http => http.HttpRequestError,
        _ => System.Net.Http.HttpRequestError.Unknown
      };
      if (requestError != System.Net.Http.HttpRequestError.Unknown)
        httpRequestError = requestError.ToString();
      if (status is null && cause is HttpRequestException { StatusCode: { } httpStatus } && (int)httpStatus is >= 100 and <= 599)
        status = (int)httpStatus;
      switch (cause) {
        case SocketException socket:
          socketError = socket.SocketErrorCode.ToString();
          break;
        case WebSocketException socket:
          webSocketError = socket.WebSocketErrorCode.ToString();
          break;
        case ResponsesSocketStream.ReceiveException receive:
          webSocketReceiveFailure = receive.Failure.ToString();
          exceptionType = receive.TransportCause?.ExceptionType ?? exceptionType;
          httpRequestError = receive.TransportCause?.HttpRequestError ?? httpRequestError;
          socketError = receive.TransportCause?.SocketError ?? socketError;
          webSocketError = receive.TransportCause?.WebSocketError ?? webSocketError;
          break;
      }
    }
    return new(status, exceptionType, httpRequestError, socketError, webSocketError, webSocketReceiveFailure);
  }
}
