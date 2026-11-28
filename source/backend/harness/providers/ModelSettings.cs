namespace Urd.Core.Providers;

// Urd-owned model metadata, independent of a provider's connection or endpoint.
public enum ModelFamily { OpenAI, Anthropic, Google, Other }

// Per-connection overrides and learned capabilities, independent of catalog and credentials.
public sealed record ModelSettings(Guid ConnectionId, string ModelId, ModelFamily Family,
    bool SupportsImages, int? ContextWindow = null, bool FamilyExplicit = true);
