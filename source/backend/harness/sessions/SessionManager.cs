using Microsoft.Extensions.AI;
using Urd.Core.Storage;

namespace Urd.Core.Sessions;

// Creates and opens sessions, keeping one live instance per session ID.
public sealed class SessionManager {
    private readonly SessionStore _store;
    private readonly IChatClient _chatClient;
    private readonly AsyncLock _lock = new();
    private readonly Dictionary<Guid, PrimarySession> _sessions = new();

    public SessionManager(SessionStore store, IChatClient chatClient) {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(chatClient);
        _store = store;
        _chatClient = chatClient;
    }

    public async Task<PrimarySession> CreatePrimaryAsync() {
        using var held = await _lock.AcquireAsync();
        SessionRecord record = _store.Sessions.CreatePrimary(Guid.NewGuid());
        return Track(new PrimarySession(record, _store, _chatClient));
    }

    public async Task<PrimarySession> GetPrimaryAsync(Guid sessionId) {
        using var held = await _lock.AcquireAsync();
        if (_sessions.TryGetValue(sessionId, out PrimarySession? cached))
            return cached;

        SessionRecord record = _store.Sessions.Read(sessionId)
            ?? throw new KeyNotFoundException($"Session '{sessionId}' does not exist.");
        return Track(new PrimarySession(record, _store, _chatClient));
    }

    private PrimarySession Track(PrimarySession session) {
        _sessions.Add(session.Id, session);
        return session;
    }
}
