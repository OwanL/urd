using Urd.Core.Context;

namespace Urd.Core.Sessions;

public sealed partial class SessionManager {
  // Discover children from this manager's registry without opening or subscribing to any session.
  public Task<IReadOnlyList<SubagentDiscovery>> ListLiveSubagentsAsync(Guid primaryId,
      CancellationToken cancellation = default) => registry.RunOpenAsync(() => {
        if (store.Sessions.Read(primaryId) is not { Kind: SessionKind.Primary })
          throw new KeyNotFoundException($"Primary session '{primaryId}' does not exist.");

        return store.Sessions.ReadSubagentDiscoveries(registry.LiveDescendantIds(primaryId));
      }, cancellation);

  public Task<SubagentDiscovery?> FindSubagentByCallAsync(Guid parentId, Guid requestId, string callId,
      CancellationToken cancellation = default) => registry.RunOpenAsync(() => {
        // A retained child without a title has no remaining naming work.
        SubagentDiscovery? child = store.Sessions.FindSubagentByCall(parentId, requestId, callId);
        return child is { TitlePending: true } && registry.FindChild(child.Id) is null
            ? child with { TitlePending = false } : child;
      }, cancellation);
}
