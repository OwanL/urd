using System.Buffers.Binary;
using Microsoft.Data.Sqlite;

namespace Urd.Core.Context;

// This is a derived cache, not part of the session store. The catalog serializes all access.
internal sealed class InstructionResourceCatalogIndex : IDisposable {
  private const int applicationId = 0x55524449; // URDI
  private const int version = 3; // The filename scan now includes CLAUDE.md and GEMINI.md.
  private readonly SqliteConnection connection;

  private InstructionResourceCatalogIndex(SqliteConnection connection) => this.connection = connection;

  internal static InstructionResourceCatalogIndex Open(string path, StringComparer names, bool allowRebuild = true) {
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    var options = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false };
    var connection = new SqliteConnection(options.ToString());
    try {
      connection.Open();
      connection.CreateCollation("path_identity", names.Compare);
      long owner = (long)Scalar(connection, "PRAGMA application_id");
      long schema = (long)Scalar(connection, "PRAGMA user_version");
      if (owner != applicationId && (owner != 0 || schema != 0 ||
          (long)Scalar(connection, "SELECT count(*) FROM sqlite_schema") != 0))
        throw new InvalidOperationException("The instruction-resource index path belongs to another database.");
      if (owner == applicationId && (schema != version ||
          !Equals(Scalar(connection, "PRAGMA quick_check"), "ok") ||
          (long)Scalar(connection, "SELECT count(*) FROM sqlite_schema WHERE type = 'table' AND name IN ('resource_paths', 'scanned_roots')") != 2)) {
        connection.Dispose();
        return Rebuild(path, names);
      }
      if (owner == 0) {
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
          CREATE TABLE resource_paths(path TEXT PRIMARY KEY COLLATE path_identity) WITHOUT ROWID;
          CREATE TABLE scanned_roots(path TEXT PRIMARY KEY COLLATE path_identity) WITHOUT ROWID;
          PRAGMA application_id = 0x55524449;
          PRAGMA user_version = 3;
          """;
        command.ExecuteNonQuery();
        transaction.Commit();
      }
      return new(connection);
    } catch (SqliteException exception) when (allowRebuild && exception.SqliteErrorCode is 11 or 26) {
      connection.Dispose();
      if (HasCatalogHeader(path))
        return Rebuild(path, names);
      throw new InvalidOperationException("The instruction-resource index cannot be opened. If this file is a derived catalog, remove it and retry.", exception);
    } catch {
      connection.Dispose();
      throw;
    }
  }

  private static InstructionResourceCatalogIndex Rebuild(string path, StringComparer names) {
    foreach (string suffix in new[] { "", "-journal", "-wal", "-shm" })
      File.Delete(path + suffix);
    return Open(path, names, allowRebuild: false);
  }

  private static bool HasCatalogHeader(string path) {
    // SQLite may fail before it can execute the ownership pragma. Never erase an unrelated file.
    using var stream = File.OpenRead(path);
    Span<byte> header = stackalloc byte[72];
    return stream.ReadAtLeast(header, header.Length, false) == header.Length &&
        header[..16].SequenceEqual("SQLite format 3\0"u8) && BinaryPrimitives.ReadInt32BigEndian(header[68..]) == applicationId;
  }

  private static object Scalar(SqliteConnection connection, string sql) {
    using var command = connection.CreateCommand();
    command.CommandText = sql;
    return command.ExecuteScalar()!;
  }

  internal string[] Read(string directory) => ReadDescendants(directory, transaction: null);

  internal void Apply(string? scannedRoot, IReadOnlyList<InstructionResourcePathUpdate> updates) {
    if (updates.Count == 0 && scannedRoot is null)
      return;

    using var transaction = connection.BeginTransaction();
    using var delete = connection.CreateCommand();
    delete.Transaction = transaction;
    delete.CommandText = "DELETE FROM resource_paths WHERE path = $path";
    var deletedPath = delete.Parameters.Add("$path", SqliteType.Text);
    using var insert = connection.CreateCommand();
    insert.Transaction = transaction;
    insert.CommandText = "INSERT INTO resource_paths(path) VALUES ($path)";
    var insertedPath = insert.Parameters.Add("$path", SqliteType.Text);

    foreach (var update in updates) {
      // Keep unchanged rows, including on restart. Preserve a case-only rename on Windows.
      var remaining = new HashSet<string>(update.Paths, StringComparer.Ordinal);
      using var exact = connection.CreateCommand();
      exact.Transaction = transaction;
      exact.CommandText = "SELECT path FROM resource_paths WHERE path = $path";
      exact.Parameters.AddWithValue("$path", update.Path);
      if (exact.ExecuteScalar() is string previous && !remaining.Remove(previous)) {
        deletedPath.Value = previous;
        delete.ExecuteNonQuery();
      }
      foreach (string path in ReadDescendants(update.Path, transaction)) {
        if (remaining.Remove(path))
          continue;
        deletedPath.Value = path;
        delete.ExecuteNonQuery();
      }
      foreach (string path in remaining) {
        insertedPath.Value = path;
        insert.ExecuteNonQuery();
      }
    }

    if (scannedRoot is not null) {
      using var mark = connection.CreateCommand();
      mark.Transaction = transaction;
      mark.CommandText = """
        DELETE FROM scanned_roots WHERE path >= $start AND path < $end;
        INSERT INTO scanned_roots(path) VALUES ($path) ON CONFLICT(path) DO NOTHING;
        """;
      BindDescendantRange(mark, scannedRoot);
      mark.Parameters.AddWithValue("$path", scannedRoot);
      mark.ExecuteNonQuery();
    }
    transaction.Commit();
  }

  private string[] ReadDescendants(string directory, SqliteTransaction? transaction) {
    using var command = connection.CreateCommand();
    command.Transaction = transaction;
    command.CommandText = "SELECT path FROM resource_paths WHERE path >= $start AND path < $end ORDER BY path";
    BindDescendantRange(command, directory);
    using var rows = command.ExecuteReader();
    var paths = new List<string>();
    while (rows.Read())
      paths.Add(rows.GetString(0));
    return paths.ToArray();
  }

  private static void BindDescendantRange(SqliteCommand command, string directory) {
    string prefix = Path.EndsInDirectorySeparator(directory) ? directory : directory + Path.DirectorySeparatorChar;
    // All descendants share this separator-terminated prefix in the ordinal index.
    command.Parameters.AddWithValue("$start", prefix);
    command.Parameters.AddWithValue("$end", prefix[..^1] + (char)(prefix[^1] + 1));
  }

  public void Dispose() => connection.Dispose();
}

internal readonly record struct InstructionResourcePathUpdate(string Path, IReadOnlyCollection<string> Paths);
