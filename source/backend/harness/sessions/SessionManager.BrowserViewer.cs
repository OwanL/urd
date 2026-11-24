using Urd.Core.Context;

namespace Urd.Core.Sessions;

public sealed partial class SessionManager {
  private void OnBrowserChanged(Guid owner) {
    var seen = new HashSet<Guid>();
    while (seen.Add(owner) && store.Sessions.Read(owner) is { } record) {
      if (record.Kind == SessionKind.Primary) {
        Notify(BrowserChanged, owner);
        return;
      }
      if (record.Kind != SessionKind.Subagent || record.ParentSessionId is not Guid parent)
        return;
      owner = parent;
    }
  }

  // Registry ancestry identifies live candidates; only persisted ancestry grants viewer access.
  internal Task<IReadOnlyList<Guid>?> BrowserViewerOwnersAsync(Guid primaryId, CancellationToken cancellation = default) =>
      registry.RunOpenAsync<IReadOnlyList<Guid>?>(() => {
        cancellation.ThrowIfCancellationRequested();
        if (store.Sessions.Read(primaryId) is not { Kind: SessionKind.Primary })
          return null;

        List<Guid> owners = [primaryId];
        var belongs = new Dictionary<Guid, bool> { [primaryId] = true };
        foreach (Guid candidate in registry.LiveDescendantIds(primaryId)) {
          cancellation.ThrowIfCancellationRequested();
          var path = new HashSet<Guid>();
          Guid current = candidate;
          bool valid;
          while (!belongs.TryGetValue(current, out valid)) {
            cancellation.ThrowIfCancellationRequested();
            if (!path.Add(current) || store.Sessions.Read(current) is not {
                Kind: SessionKind.Subagent, ParentSessionId: Guid parent }) {
              valid = false;
              break;
            }
            current = parent;
          }
          foreach (Guid id in path)
            belongs[id] = valid;
          if (valid)
            owners.Add(candidate);
        }
        return owners;
      }, cancellation);
}
