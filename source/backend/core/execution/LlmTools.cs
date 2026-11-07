using System.Text.Json;

namespace Urd.Core.Execution;

// One complete LLM tool call, frozen before the authorized batch is saved.
internal sealed record LlmToolCall(string CallId, string Name, JsonElement Arguments);

// A completed invocation or error result, not a claim that a command's intended effect succeeded.
internal sealed record LlmToolResult(
    string CallId, ExecutionOutcome Outcome, JsonElement Result, string? Error = null);
