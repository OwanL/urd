using Urd.Core.Preferences;
using Urd.Core.Sessions;

namespace Urd.Core.Context;

// How one kind of starting resource is identified, described to the selector model and recovered on failure.
internal sealed record PruningCategory<T>(
    string Name,
    AuxiliaryModelPurpose Purpose,
    string Guidance,
    Func<T, string> Identity,
    Func<T, PrepassResource> Resource,
    Func<IReadOnlyList<PruningCandidate<T>>, object[]> Manifest) {
  // Short ids such as "s1" keep the manifest compact. Without a prefix, the identity is the model-facing id.
  internal char? ModelIdPrefix { get; init; }

  // When set, a context overflow is retried without the largest candidates by this size.
  internal Func<T, int>? Size { get; init; }

  // When selection fails, keep every candidate instead of only the protected resources.
  internal bool RetainsAllOnFailure { get; init; }

  internal PruningCandidate<T>[] ToCandidates(IReadOnlyList<T> items) => items
      .Select((item, index) => new PruningCandidate<T>(item, Identity(item), ModelId(item, index)))
      .ToArray();

  private string ModelId(T item, int index) => ModelIdPrefix is { } prefix ? $"{prefix}{index + 1}" : Identity(item);
}
