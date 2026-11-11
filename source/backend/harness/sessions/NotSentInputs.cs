namespace Urd.Core.Sessions;

// Terminal inputs whose containing request is known never to have started sending.
// Full source remains on the retained receipt, not on this bounded listing.
public sealed record NotSentInput(Guid SubmissionId, Guid ExecutionId, string Preview);

public sealed record NotSentPage(IReadOnlyList<NotSentInput> Inputs, string? NextCursor);
