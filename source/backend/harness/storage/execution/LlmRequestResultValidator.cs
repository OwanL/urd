using System.Text.Json;
using Urd.Core.Execution;
using Urd.Core.Providers;

namespace Urd.Core.Storage;

// Freezes and validates the provider result before either request store changes durable state.
internal static class LlmRequestResultValidator {
  internal static ValidatedLlmRequestResult Validate(LlmRequestResult completion) {
    ArgumentNullException.ThrowIfNull(completion);
    if (!Enum.IsDefined(completion.Outcome))
      throw new ArgumentOutOfRangeException(nameof(completion));

    if (completion.ToolCalls is { Count: > 16 })
      throw new ArgumentException("A response may authorize at most 16 tool calls.", nameof(completion));

    var calls = (completion.ToolCalls ?? []).Select(call => {
      string arguments = StorageText.JsonText(call.Arguments, requireObject: true);
      return (call.CallId, call.Name, Arguments: arguments, Excerpt: ToolArgumentsExcerpt.Create(arguments));
    }).ToArray();
    if (calls.Any(call => string.IsNullOrWhiteSpace(call.CallId) || string.IsNullOrWhiteSpace(call.Name))
        || calls.Select(call => call.CallId).Distinct(StringComparer.Ordinal).Count() != calls.Length
        || (calls.Length != 0 && completion.Outcome != ExecutionOutcome.Completed))
      throw new ArgumentException("Only a completed response may authorize up to 16 distinct, named tool calls.", nameof(completion));

    string? providerResponseJson = null;
    if (completion.ProviderResponse is { } response) {
      if (completion.Outcome != ExecutionOutcome.Completed)
        throw new ArgumentException("Only a completed response may retain provider context.", nameof(completion));
      _ = LlmProviderContext.Restore(response);
      providerResponseJson = JsonSerializer.Serialize(response);
    }
    string? usageJson = completion.Usage is null ? null : JsonSerializer.Serialize(completion.Usage);
    return new(calls, providerResponseJson, usageJson);
  }
}

internal sealed record ValidatedLlmRequestResult(
    (string CallId, string Name, string Arguments, string Excerpt)[] Calls,
    string? ProviderResponseJson,
    string? UsageJson);
