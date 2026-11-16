namespace Urd.Core.Context;

// The original candidate objects retained by one resource-pruning operation.
internal sealed record StartingResourcePruningResult<T>(
  IReadOnlyList<T> Retained,
  bool IsFallback = false,
  string? Error = null);
