namespace Urd.Backend.Tests.Support;

// Reads persisted submission receipts directly from a test database.
internal static class StoredSubmissions {
  internal static List<StoredSubmission> Read(TemporaryDatabase database) => database.ReadRows(
      "SELECT input_id, run_id FROM input_decisions ORDER BY input_id;",
      row => new StoredSubmission(Guid.Parse(row.GetString(0)),
          row.IsDBNull(1) ? null : Guid.Parse(row.GetString(1))));
}

// Persisted receipt row: the submission and its execution, if accepted.
internal sealed record StoredSubmission(Guid SubmissionId, Guid? ExecutionId);
