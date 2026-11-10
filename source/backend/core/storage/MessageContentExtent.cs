using Urd.Core.Sessions;

namespace Urd.Core.Storage;

// Fixed identity and committed bounds, captured before source reads begin.
internal sealed record MessageContentExtent(
    Guid SessionId, MessageContentCursor From, MessageContentCursor AvailableThrough,
    bool IsClosed, bool FinalOutputCommitted, Guid SubmissionId, Guid RequestId);

// One bounded storage read. Decoding and source assembly happen after its transaction ends.
internal sealed record MessageContentSegment(long ByteLength, byte[] Prefix);
