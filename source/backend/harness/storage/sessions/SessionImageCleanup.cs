using Microsoft.Data.Sqlite;
using Urd.Core.MarkdownRendering;

namespace Urd.Core.Storage;

// Content-addressed files may be shared. Check references after deleting the owning rows,
// then unlink only after commit while image publication is still blocked by the database gate.
internal sealed class SessionImageCleanup(Database database, string toolDirectory, string markdownDirectory) {
  private const string ToolHash = "json_extract(CASE WHEN j.type = 'object' THEN j.value END, '$.hash')";
  private const string ResultImages = "json_each(CASE WHEN json_valid(t.result_json) THEN t.result_json END, '$.images') j";
  private const string ToolHashes = $"""
      SELECT {ToolHash} AS hash FROM execution_tools t, {ResultImages}
      UNION ALL
      SELECT {ToolHash} FROM auxiliary_tools t, {ResultImages}
      """;

  internal void CollectCandidates(SqliteTransaction transaction) => database.Execute(transaction, $"""
      CREATE TEMP TABLE closing_tool_images (hash TEXT PRIMARY KEY NOT NULL) WITHOUT ROWID;
      CREATE TEMP TABLE closing_markdown_images (hash TEXT PRIMARY KEY NOT NULL) WITHOUT ROWID;
      INSERT OR IGNORE INTO closing_tool_images
      SELECT {ToolHash} FROM execution_runs r JOIN execution_tools t ON r.run_id = t.run_id, {ResultImages}
      WHERE r.session_id IN closing_sessions
      UNION ALL
      SELECT {ToolHash} FROM execution_runs r JOIN auxiliary_conversations c ON c.run_id = r.run_id
      JOIN auxiliary_tools t ON t.conversation_id = c.conversation_id, {ResultImages}
      WHERE r.session_id IN closing_sessions;
      INSERT OR IGNORE INTO closing_markdown_images
      SELECT i.file_hash FROM execution_messages m JOIN markdown_images i ON i.message_ordinal = m.ordinal
      WHERE m.session_id IN closing_sessions;
      """);

  internal (List<string> Tool, List<string> Markdown) ReadOrphans(SqliteTransaction transaction) {
    List<string> tool = database.Scalar<bool>(transaction, "SELECT EXISTS(SELECT 1 FROM closing_tool_images);")
        ? database.Query(transaction, $"""
            SELECT hash FROM closing_tool_images
            EXCEPT SELECT hash FROM ({ToolHashes}) WHERE hash IN closing_tool_images;
            """, row => row.String("hash")) : [];
    List<string> markdown = database.Query(transaction, """
        SELECT c.hash FROM closing_markdown_images c
        WHERE NOT EXISTS (SELECT 1 FROM markdown_images WHERE file_hash = c.hash);
        """, row => row.String("hash"));
    database.Execute(transaction, "DROP TABLE closing_tool_images; DROP TABLE closing_markdown_images;");
    return (tool, markdown);
  }

  internal void Delete(List<string> tool, List<string> markdown) {
    DeleteFiles(toolDirectory, tool, null);
    DeleteFiles(markdownDirectory, markdown, null);
  }

  // Startup has the exclusive profile lease and there are no live imports. This also retries
  // unlinks interrupted between a committed database deletion and filesystem cleanup.
  internal void RemoveUnreferencedFiles() => database.RunLocked(() => {
    DeleteFiles(toolDirectory, null, () => database.Query(null, $"""
        SELECT DISTINCT hash FROM ({ToolHashes}) WHERE hash IS NOT NULL;
        """, row => row.String("hash")).ToHashSet(StringComparer.Ordinal));
    DeleteFiles(markdownDirectory, null, () => database.Query(null,
        "SELECT DISTINCT file_hash FROM markdown_images;", row => row.String("file_hash")).ToHashSet(StringComparer.Ordinal));
    return true;
  });

  private static void DeleteFiles(string directory, List<string>? orphans, Func<HashSet<string>>? readReferenced) {
    if (orphans is { Count: 0 })
      return;
    try {
      if (!Directory.Exists(directory) || File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint))
        return;
      if (orphans is not null) {
        foreach (string hash in orphans.Where(MarkdownImageTarget.IsKey))
          DeleteFile(Path.Combine(directory, hash));
        return;
      }

      using var paths = Directory.EnumerateFiles(directory).GetEnumerator();
      if (!paths.MoveNext())
        return;
      HashSet<string> referenced = readReferenced!();
      do {
        string name = Path.GetFileName(paths.Current);
        if (IsInterruptedAsset(name) || (MarkdownImageTarget.IsKey(name) && !referenced.Contains(name)))
          DeleteFile(paths.Current);
      } while (paths.MoveNext());
    } catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) {
      // A later exclusive startup can retry; no retained row points to a removed file.
    }
  }

  private static bool IsInterruptedAsset(string name) => name.Length == 102 && name[0] == '.' && name[65] == '.'
      && name.EndsWith(".tmp", StringComparison.Ordinal) && MarkdownImageTarget.IsKey(name[1..65])
      && Guid.TryParseExact(name[66..98], "N", out _);

  private static void DeleteFile(string path) {
    try {
      File.Delete(path);
    } catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) {
      // A later exclusive startup can retry a locked file.
    }
  }
}
