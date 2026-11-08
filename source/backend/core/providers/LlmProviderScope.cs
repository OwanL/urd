namespace Urd.Core.Providers;

internal sealed record LlmProviderScope(Guid ConnectionId, long Generation, LlmApi Api, string ModelId);
