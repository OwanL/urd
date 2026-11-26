using Urd.Core.Context;
using Urd.Core.Storage;

namespace Urd.Core.Sessions;

// Resolves persisted session ancestry for the shared browser: which primary owns a browser
// owner's content and which live sessions may view a primary's browser. Registry ancestry only
// identifies live candidates; persisted ancestry grants viewer access.
internal sealed class BrowserViewerAncestry(UrdStore store, SessionRegistry registry) {

  // Follows persisted parents, stopping on a missing session, a non-subagent branch or a cycle.
  internal Guid? OwningPrimary(Guid owner) {
    var seen = new HashSet<Guid>();
    while (seen.Add(owner) && store.Sessions.Read(owner) is { } record) {
      if (record.Kind == SessionKind.Primary)
        return owner;
      if (record.Kind != SessionKind.Subagent || record.ParentSessionId is not Guid parent)
        return null;
      owner = parent;
    }
    return null;
  }

  internal Task<IReadOnlyList<Guid>?> OwnersAsync(Guid primaryId, CancellationToken cancellation = default) =>
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
