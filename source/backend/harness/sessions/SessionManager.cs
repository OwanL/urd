using Microsoft.Extensions.AI;
using Urd.Core.Storage;

namespace Urd.Core.Sessions;

// Creates and opens sessions, keeping one live instance per session ID.
public sealed class SessionManager {
  private readonly SessionStore store;
  private readonly IChatClient chatClient;
  private readonly AsyncLock gate = new();
  private readonly Dictionary<Guid, PrimarySession> sessions = new();

  public SessionManager(SessionStore store, IChatClient chatClient) {
    ArgumentNullException.ThrowIfNull(store);
    ArgumentNullException.ThrowIfNull(chatClient);
    this.store = store;
    this.chatClient = chatClient;
  }

  public async Task<PrimarySession> CreatePrimaryAsync() {
    using var held = await gate.AcquireAsync();
    SessionRecord record = store.Sessions.CreatePrimary(Guid.NewGuid());
    return Track(new PrimarySession(record, store, chatClient));
  }

  public async Task<PrimarySession> GetPrimaryAsync(Guid sessionId) {
    using var held = await gate.AcquireAsync();
    if (sessions.TryGetValue(sessionId, out PrimarySession? cached))
      return cached;

    SessionRecord record = store.Sessions.Read(sessionId)
        ?? throw new KeyNotFoundException($"Session '{sessionId}' does not exist.");
    return Track(new PrimarySession(record, store, chatClient));
  }

  private PrimarySession Track(PrimarySession session) {
    sessions.Add(session.Id, session);
    return session;
  }
}
