using Urd.Core.Execution;

namespace Urd.Core.Sessions;

// Short prefix of one message in a snapshot, with a cursor for reading the rest.
public sealed record MessagePreview(
    MessageRef Message, string Preview, bool Truncated,
    MessageContentCursor AvailableThrough, bool FinalOutputCommitted,
    ExecutionOutcome? Settlement);
