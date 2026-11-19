using Urd.Core.Providers;

namespace Urd.Core.Sessions;

// One deliberate suffix replacement. Retrying must preserve the entire original command.
public sealed record MessageEditSubmission(
    Guid OperationId, MessageRef Message, string Text, LlmSelection LlmSelection);

// A committed replacement, not a claim that the new execution has succeeded.
public sealed record MessageEditReceipt(
    Guid SessionId, Guid OperationId, long HistoryRevision, Guid ExecutionId);
