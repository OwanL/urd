using Microsoft.Extensions.AI;

namespace Urd.Core.Providers;

// The complete secret-free choice frozen by an initiating Send, not a live connection lookup.
public sealed record LlmSelection(ProviderConnection Connection, string ModelId, string? ReasoningEffort) {
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
