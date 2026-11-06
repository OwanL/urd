namespace Urd.Core.Execution;

// Buffered response text sent through SessionBase for persistence.
// IsFinal closes the text, not the run; the final chunk may be empty.
internal sealed record LlmResponseChunk(
    Guid RequestId,
    Guid ResponseId,
    long ChunkIndex,
    string Text,
    bool IsFinal);
