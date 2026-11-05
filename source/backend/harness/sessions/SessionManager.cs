using Urd.Core.Storage;

namespace Urd.Core.Sessions;

public sealed class SessionManager
{
    private readonly SessionStore _store;
    private readonly SemaphoreSlim _registryGate = new(1, 1);
    private readonly Dictionary<Guid, SessionBase> _sessions = new();

    public SessionManager(SessionStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    public async Task<PrimarySession> CreatePrimaryAsync()
    {
        await _registryGate.WaitAsync().ConfigureAwait(false);
        try
        {
            SessionRecord record = _store.CreatePrimary(Guid.NewGuid());
            var session = new PrimarySession(record);
            _sessions.Add(session.Id, session);
            return session;
        }
        finally
        {
            _registryGate.Release();
        }
    }

    public async Task<PrimarySession> GetPrimaryAsync(Guid sessionId)
    {
        await _registryGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_sessions.TryGetValue(sessionId, out SessionBase? cached))
            {
                return cached as PrimarySession
                    ?? throw new InvalidOperationException($"Session '{sessionId}' is not a primary session.");
            }

            SessionRecord record = _store.ReadSession(sessionId)
                ?? throw new KeyNotFoundException($"Session '{sessionId}' does not exist.");
            var session = new PrimarySession(record);
            _sessions.Add(session.Id, session);
            return session;
        }
        finally
        {
            _registryGate.Release();
        }
    }
}
