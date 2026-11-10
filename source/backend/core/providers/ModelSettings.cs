namespace Urd.Core.Providers;

// Explicit per-connection model capabilities, independent of catalog and credentials.
public sealed record ModelSettings(
  Guid ConnectionId, string ModelId, ModelFamily Family, bool SupportsImages);
