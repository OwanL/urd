using Microsoft.Data.Sqlite;
using Urd.Core.Sessions;

namespace Urd.Core.Storage;

// Creates and reads durable session identities and history revisions.
internal sealed class SessionStorage(Database database) {
    internal SessionRecord CreatePrimary(Guid sessionId) {
        if (sessionId == Guid.Empty) {
            throw new ArgumentException("Session ID cannot be empty.", nameof(sessionId));
        }

        return database.Write(transaction => {
            using SqliteCommand command = database.Command(transaction, """
                INSERT INTO sessions (session_id, history_revision) VALUES ($id, 0)
                RETURNING session_id, history_revision;
                """, ("$id", sessionId));
            using SqliteDataReader reader = command.ExecuteReader();
            reader.Read();
            return ReadRecord(reader);
        });
    }

    internal SessionRecord? Read(Guid sessionId) {
        return database.WithoutTransaction<SessionRecord?>(() => {
            using SqliteCommand command = database.Command(null,
                "SELECT session_id, history_revision FROM sessions WHERE session_id = $id;", ("$id", sessionId));
            using SqliteDataReader reader = command.ExecuteReader();
            return reader.Read() ? ReadRecord(reader) : null;
        });
    }

    private static SessionRecord ReadRecord(SqliteDataReader reader)
        => new(reader.GetGuid(0), reader.GetInt64(1));
}
