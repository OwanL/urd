using System.Text.Json;

namespace Urd.Core.Providers;

public enum ProviderStreamTerminalKind { Completed, Incomplete, ResponseFailed, StandaloneError }
public enum ProviderStreamTerminalCorrelation { Matched, Missing, Mismatch, NotApplicable }
public enum ProviderStreamTerminalStatus { Missing, Malformed, Completed, Incomplete, Failed, InProgress, Queued, Cancelled, Other }
public enum ProviderStreamTerminalErrorShape { Missing, Null, ErrorObject, SiblingFields, Other }
public enum ProviderStreamTerminalErrorCode { Missing, Malformed, ServerError, RateLimitExceeded, InvalidRequestError, Other }

// Contains only bounded categories; never retain provider strings or identifiers.
public sealed record ProviderStreamTerminal(ProviderStreamTerminalKind Kind, ProviderStreamTerminalCorrelation Correlation,
    ProviderStreamTerminalStatus Status, ProviderStreamTerminalErrorShape ErrorShape,
    ProviderStreamTerminalErrorCode ErrorCode, bool MessagePresent, int? EventStatusCode);

internal static class ResponsesTerminalObservation {
  internal static ProviderStreamTerminal? Read(JsonElement root, string? responseId, string? type) {
    ProviderStreamTerminalKind? kind = type switch {
      "response.completed" => ProviderStreamTerminalKind.Completed,
      "response.incomplete" => ProviderStreamTerminalKind.Incomplete,
      "response.failed" => ProviderStreamTerminalKind.ResponseFailed,
      "error" => ProviderStreamTerminalKind.StandaloneError,
      _ => null
    };
    if (kind is null)
      return null;

    bool hasResponse = root.TryGetProperty("response", out var response) && response.ValueKind == JsonValueKind.Object;
    var correlation = Correlation(root, hasResponse ? response : default, responseId, kind.Value);
    var status = hasResponse ? Status(response) : ProviderStreamTerminalStatus.Missing;
    JsonElement errorParent = hasResponse ? response : default;
    if (kind == ProviderStreamTerminalKind.StandaloneError)
      errorParent = root;
    var (shape, code, message) = Error(errorParent, kind == ProviderStreamTerminalKind.StandaloneError);
    int? eventStatusCode = kind == ProviderStreamTerminalKind.StandaloneError && root.TryGetProperty("status", out var eventStatus) &&
        eventStatus.ValueKind == JsonValueKind.Number && eventStatus.TryGetInt32(out int number) && number is >= 100 and <= 599
        ? number : null;
    return new(kind.Value, correlation, status, shape, code, message, eventStatusCode);
  }

  private static ProviderStreamTerminalCorrelation Correlation(JsonElement root, JsonElement response, string? responseId,
      ProviderStreamTerminalKind kind) {
    bool hasEventId = root.TryGetProperty("response_id", out var eventId);
    JsonElement objectId = default;
    bool hasObjectId = response.ValueKind == JsonValueKind.Object && response.TryGetProperty("id", out objectId);
    if (kind == ProviderStreamTerminalKind.StandaloneError && !hasEventId && !hasObjectId)
      return ProviderStreamTerminalCorrelation.NotApplicable;
    if (responseId is null)
      return ProviderStreamTerminalCorrelation.Missing;
    if (hasEventId && !Matches(eventId, responseId) || hasObjectId && !Matches(objectId, responseId))
      return ProviderStreamTerminalCorrelation.Mismatch;
    if (kind != ProviderStreamTerminalKind.StandaloneError && !hasObjectId || !hasEventId && !hasObjectId)
      return ProviderStreamTerminalCorrelation.Missing;
    return ProviderStreamTerminalCorrelation.Matched;
  }

  private static bool Matches(JsonElement id, string? responseId) =>
      id.ValueKind == JsonValueKind.String && responseId is not null && id.GetString() == responseId;

  private static ProviderStreamTerminalStatus Status(JsonElement response) {
    if (!response.TryGetProperty("status", out var value))
      return ProviderStreamTerminalStatus.Missing;
    if (value.ValueKind != JsonValueKind.String)
      return ProviderStreamTerminalStatus.Malformed;
    return value.GetString() switch {
      "completed" => ProviderStreamTerminalStatus.Completed,
      "incomplete" => ProviderStreamTerminalStatus.Incomplete,
      "failed" => ProviderStreamTerminalStatus.Failed,
      "in_progress" => ProviderStreamTerminalStatus.InProgress,
      "queued" => ProviderStreamTerminalStatus.Queued,
      "cancelled" => ProviderStreamTerminalStatus.Cancelled,
      _ => ProviderStreamTerminalStatus.Other
    };
  }

  private static (ProviderStreamTerminalErrorShape, ProviderStreamTerminalErrorCode, bool) Error(JsonElement parent, bool standalone) {
    if (parent.ValueKind != JsonValueKind.Object)
      return (ProviderStreamTerminalErrorShape.Missing, ProviderStreamTerminalErrorCode.Missing, false);
    if (!parent.TryGetProperty("error", out var error)) {
      if (standalone && (parent.TryGetProperty("code", out _) || parent.TryGetProperty("message", out _)))
        return (ProviderStreamTerminalErrorShape.SiblingFields, Code(parent), parent.TryGetProperty("message", out _));
      return (ProviderStreamTerminalErrorShape.Missing, ProviderStreamTerminalErrorCode.Missing, false);
    }
    if (error.ValueKind == JsonValueKind.Null)
      return (ProviderStreamTerminalErrorShape.Null, ProviderStreamTerminalErrorCode.Missing, false);
    if (error.ValueKind != JsonValueKind.Object)
      return (ProviderStreamTerminalErrorShape.Other, ProviderStreamTerminalErrorCode.Missing, false);
    return (ProviderStreamTerminalErrorShape.ErrorObject, Code(error), error.TryGetProperty("message", out _));
  }

  private static ProviderStreamTerminalErrorCode Code(JsonElement error) {
    if (!error.TryGetProperty("code", out var value))
      return ProviderStreamTerminalErrorCode.Missing;
    if (value.ValueKind != JsonValueKind.String)
      return ProviderStreamTerminalErrorCode.Malformed;
    return value.GetString() switch {
      "server_error" => ProviderStreamTerminalErrorCode.ServerError,
      "rate_limit_exceeded" => ProviderStreamTerminalErrorCode.RateLimitExceeded,
      "invalid_request_error" => ProviderStreamTerminalErrorCode.InvalidRequestError,
      _ => ProviderStreamTerminalErrorCode.Other
    };
  }
}
