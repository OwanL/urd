using Microsoft.Extensions.AI;

namespace Urd.Core.Providers;

// The complete secret-free choice frozen by an initiating Send, not a live connection lookup.
public sealed record LlmSelection(
    ProviderConnection Connection, string ModelId, string? ReasoningEffort, ModelFamily Family, bool SupportsImages,
    int? ContextWindow = null) {
  internal void Validate() {
    if (Connection is null || Connection.Id == Guid.Empty || string.IsNullOrWhiteSpace(ModelId))
      throw new ArgumentException("An LLM selection requires a connection and model.");
    _ = ReadReasoningEffort();
    ModelContextWindows.Validate(ContextWindow, nameof(ContextWindow));
    if (!Enum.IsDefined(Family) || !Enum.IsDefined(Connection.Route) || !Enum.IsDefined(Connection.Api)
        || !Enum.IsDefined(Connection.Authentication) || Connection.Name is null
        || Connection.Endpoint is not { IsAbsoluteUri: true } endpoint
        || (endpoint.Scheme != Uri.UriSchemeHttps && endpoint.Scheme != Uri.UriSchemeHttp))
      throw new ArgumentException("The selected connection binding is invalid.");
  }

  // These are the standard effort levels in the pinned MEAI SDK, not model capabilities.
  internal Microsoft.Extensions.AI.ReasoningEffort? ReadReasoningEffort() => ReasoningEffort switch {
    null => null,
    "none" => Microsoft.Extensions.AI.ReasoningEffort.None,
    "low" => Microsoft.Extensions.AI.ReasoningEffort.Low,
    "medium" => Microsoft.Extensions.AI.ReasoningEffort.Medium,
    "high" => Microsoft.Extensions.AI.ReasoningEffort.High,
    "extraHigh" => Microsoft.Extensions.AI.ReasoningEffort.ExtraHigh,
    _ => throw new ArgumentException("Reasoning effort must be none, low, medium, high or extraHigh.", nameof(ReasoningEffort))
  };
}
