namespace Urd.Core.Context;

// One unprotected resource offered to the selector model under its model-facing id.
internal sealed record PruningCandidate<T>(T Item, string Identity, string ModelId);
