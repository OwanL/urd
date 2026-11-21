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

  public Task<SubagentView?> ReadSubagentViewAsync(Guid sessionId, CancellationToken cancellation = default) =>
      registry.RunOpenAsync(() => store.Sessions.ReadSubagentView(sessionId, registry.FindChild(sessionId) is not null),
          cancellation);
}
