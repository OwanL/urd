# sql-per-session — family 8 (per-session SQLite + coordination DB)

`Urd.TranscriptHarness.Adapter.SqlitePerSession.SqlitePerSessionTranscriptStore` —
freezing the `ITranscriptStore` + `IStoreStatistics` contract; public
parameterless constructor; net10; pinned Microsoft.Data.Sqlite 10.0.14.

## Layout

FAMILY-8 layout, as declared in `STORAGE-EXPERIMENT.md`: one small SQLite
database PER SESSION plus one shared coordination database.

```
<storeRoot>/coordination.db                      assignments (authority)
                                                 + pending_deletions (deletion intent)
<storeRoot>/sessions/s-<esc>.<h16>/              one directory per session
  transcript.db (-wal/-shm)                      transcript content for that session
```

- **Assignments live in the coordinator** and are served for every assignment
  ever created, including after their target session was deleted
  (`GetAssignment` works for deleted targets).
- **Per-session file presence is the authority** for session existence,
  message counts and all transcript content.
- **Session-internal storage reuses the rows adapter** (`SqliteRowsTranscriptStore`,
  project reference — not duplicated): each `transcript.db` is one rows store
  instance with its own WAL; appends, per-call persisted stream batches,
  finalize, suffix delete+insert in one transaction, bounded write-time
  previews, targeted fetch and window semantics are exactly the rows
  adapter's tested behavior. This adapter adds only file placement, the
  coordination DB, lazy open/remove and the deletion protocol.

### Session directory names

`s-` + `Uri.EscapeDataString(id)` + `.` + first-16-hex of SHA-256(id).
URI escaping is injective (percent-encoding covers every unsafe path byte, and
`%` itself is always escaped); on case-insensitive filesystems ids differing
only in case would share an escaped base name, so the id-hash suffix
disambiguates; the full id also lives inside the file. The `s-` prefix keeps
Windows-device-style ids (`CON`, `AUX`) out of the reserved-name trap.
**Declared bound:** session ids must remain short enough to form legal file
paths; harness-generated ids qualify.

## Deletion protocol (each step one durable commit)

1. one coordinator transaction: enforce the target-session invariant
   (`RequireSettled` fails while open assignments exist; `CancelOpen` cancels
   them in the same transaction — visible afterwards) **and** insert the
   `pending_deletions` intent marker;
2. filesystem action: dispose the pooled session instance, purge idle pooled
   SQLite connections (*Microsoft.Data.Sqlite pools closed connections by file
   name; pooled native handles would keep `transcript.db` locked*), then
   `File.Delete` `transcript.db` + `.db-wal` + `.db-shm` (existence-tolerant);
3. one coordinator transaction: delete the intent marker.

A kill in flight leaves exactly one recoverable state: committed marker with
(partially removed) files behind. `Open()` recovery scans `pending_deletions`,
removes leftover session files and clears markers **before serving anything**.

Create-vs-delete race defense: any mutation to a session clears a stale intent
marker BEFORE its file is created, so recovered removals can never remove
acknowledged content of a later-created session.

## Recovery limits (declared honestly)

- unacknowledged mutations inside ONE session file recover via that file's WAL
  (before-state or committed after-state, never partial);
- interrupted deletions resume at `Open()` as above; tests cover
  marker+file-present and file-gone states reopen;
- **no power-loss durability claim**: WAL + `synchronous=NORMAL` is the
  OS-flush ack tier — committed data is handed to the OS without an fsync;
- WAL is not assumed atomic across the two databases; bounded states are the
  declared deletion-intent states only (authorities above).

## Commands

```
dotnet build Urd.TranscriptHarness.Adapter.SqlitePerSession/Urd.TranscriptHarness.Adapter.SqlitePerSession.csproj -c Release -p:BuildProjectReferences=false
dotnet build tests/Urd.TranscriptHarness.Adapter.SqlitePerSession.Tests/Urd.TranscriptHarness.Adapter.SqlitePerSession.Tests.csproj -c Release -p:BuildProjectReferences=false
dotnet run tests/Urd.TranscriptHarness.Adapter.SqlitePerSession.Tests -c Release -p:BuildProjectReferences=false
dotnet run --project experiments/transcript-harness/runner/Urd.TranscriptHarness.Runner -c Release -- smoke --store-assembly <adapter dll> --store-type Urd.TranscriptHarness.Adapter.SqlitePerSession.SqlitePerSessionTranscriptStore
dotnet run --project experiments/transcript-harness/runner/Urd.TranscriptHarness.Runner -c Release -- validate --store-assembly <adapter dll> --store-type Urd.TranscriptHarness.Adapter.SqlitePerSession.SqlitePerSessionTranscriptStore
```

Fixtures are synthetic and live in OS temp only.