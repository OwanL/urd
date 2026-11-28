using Urd.Core.Context;

namespace Urd.Core.Sessions;

public sealed partial class SessionManager {
  // Unlike GetPrimaryAsync, this can read a retained child and never opens a child for execution.
  public Task<(SessionTranscript Transcript, string? WorkingDirectory)> GetTranscriptAsync(
      Guid sessionId, CancellationToken cancellation = default) => registry.RunOpenAsync(() => {
        SessionBase? open = (SessionBase?)registry.FindPrimary(sessionId) ?? registry.FindChild(sessionId);
        if (open is not null) {
          open.ThrowIfClosed();
          return (open.Transcript, open.WorkingDirectory);
        }
        SessionRecord? record = store.Sessions.Read(sessionId);
        if (record is { Kind: SessionKind.Subagent })
          return (new SessionTranscript(record.Id, store), record.WorkingDirectory);
        if (record is not { Kind: SessionKind.Primary })
          throw new KeyNotFoundException($"Session '{sessionId}' does not exist.");

        PrimarySession primary = registry.TrackPrimary(CreateSession(record));
        return (primary.Transcript, primary.WorkingDirectory);
      }, cancellation);

  // HTTP operations that require a primary treat child IDs as absent.
  public Task<PrimarySession?> FindPrimaryAsync(Guid sessionId, CancellationToken cancellation = default) =>
      registry.RunOpenAsync(() => FindPrimary(sessionId, rejectChild: false), cancellation);

  public async Task<SubagentView?> ReadSubagentViewAsync(Guid sessionId, CancellationToken cancellation = default) {
    SubagentSession? child = await registry.RunOpenAsync(() => registry.FindChild(sessionId), cancellation);
    if (child is null)
      return store.Snapshots.ReadSubagentView(sessionId, live: false).View;
    SubagentView? view = await child.ReadUnderLockAsync(() => child.ReadSubagentView(), cancellation);
    bool stillLive = await registry.RunOpenAsync(() => registry.FindChild(sessionId) == child, cancellation);
    return stillLive ? view : store.Snapshots.ReadSubagentView(sessionId, live: false).View;
  }
}
