using Microsoft.Data.Sqlite;
using Urd.TranscriptHarness.Contracts;

namespace Urd.TranscriptHarness.Adapter.SegLog;

/// <summary>
/// Family 6: segmented binary log with an explicit index
/// (STORAGE-BINDING-REVIEW bounded-acceptance variant of STORAGE-EXPERIMENT
/// family 6). Append-only immutable per-session binary segments plus one
/// SQLite coordination database that is the AUTHORITY for sessions, ordinals,
/// (segment, offset) pointers, roles, preview metadata, active draft batches,
/// and assignments.
///
/// Layout:
/// <list type="bullet">
/// <item><c>&lt;storeRoot&gt;/index.db</c> — <c>session_ptr</c> rows
/// (count, active segment, active indexed end; the append position),
/// <c>messages</c> rows (the ordinal → (segment, offset, length) map plus
/// role/preview metadata), <c>assignment</c> rows plus a target index, and
/// <c>draft_marker</c>/<c>draft_batch</c> rows.</item>
/// <item><c>&lt;storeRoot&gt;/sessions/&lt;hex(utf8(name))&gt;/seg-&lt;id:d6&gt;.log</c>
/// — immutable append-only segments; each record frame is
/// <c>[int32 LE payload length][payload]</c> (<c>SegmentFraming</c>).</item>
/// </list>
///
/// Segment budget: 8 MiB target per file. A record that does not fit rolls
/// the session to a fresh segment. A single frame exceeding the target is the
/// OVERSIZED exception: it is the only occupant of its own dedicated segment,
/// and the next append starts yet another segment (budgeted segments only
/// ever hold whole frames).
///
/// Suffix edit WITHOUT a whole-session rewrite: replacement records are
/// appended after the last indexed byte (old bytes are never rewritten), then
/// the index replaces the suffix (delete pointer rows ≥ fromOrdinal, insert
/// the new pointers, update count and pointer) in ONE transaction. The
/// superseded payload bytes remain as explicit garbage in the segments —
/// counted and reported via <see cref="RetainedGarbageBytes"/>; no compaction
/// is claimed for initial qualification. Reads resolve only indexed
/// (segment, offset) windows, so garbage is never served.
///
/// Recovery (declared, at Open):
/// <list type="bullet">
/// <item>The active segment's unindexed tail (an interrupted, unacknowledged
/// data stage) is truncated to the authoritative append position — enforced
/// even when the index count is zero (e.g. after a suffix edit down to an
/// empty session, or a fresh oversized-record slot); recovery is by content,
/// the index is authoritative, never the raw file length.</item>
/// <item>Wholly unreferenced segments (ids beyond the active pointer) are
/// deleted; the pointer advances only with an acknowledged index transaction,
/// so such bytes can never be pointed at again.</item>
/// <item>Older referenced segments that still contain garbage from superseded
/// edits are left in place (referenced bytes are never deleted), and stale
/// bytes beyond their indexed extent are never served.</item>
/// <item>Unreferenced session directories (deleted sessions) are swept.</item>
/// <item>A referenced segment that is missing, or shorter than its furthest
/// indexed byte, is corruption and fails loudly — records are never dropped
/// as "recovery".</item>
/// </list>
///
/// Durability ordering: each frame is written and Flush()ed (OS buffer cache —
/// no fsync attempted; the .NET flush-to-disk option
/// <c>FileStream.Flush(flushToDisk: true)</c> exists and is deliberately not
/// used) BEFORE the index transaction commits; a mutation is acknowledged only
/// once the index transaction commits. The tier <c>OsFlush</c> is therefore a
/// chosen, declared bound — no power-loss or fsync claim.
///
/// Streaming: every StreamDraft call commits its own transaction (batches are
/// persisted incrementally; nothing is buffered until finalize; drafts survive
/// reopening). FinalizeStream appends the frame, flushes, then commits the
/// pointer row, count, and marker/batch deletion in one transaction. Session
/// deletion follows the common contract: RequireSettled refuses while open
/// assignments target the session, CancelOpen cancels them in the same
/// transaction; settled/cancelled history survives and no dangling open
/// reference can remain.
/// </summary>
public sealed class SegLogTranscriptStore : ITranscriptStore
{
    public const string StoreName = "SegLogIndex";
    /// <summary>Default segment file budget: 8 MiB. The parameterless
    /// constructor (what the plugin loader requires) keeps that default; a
    /// test-only smaller target is selected through the internal
    /// constructor (see tests' <c>InternalsVisibleTo</c>).</summary>
    public const long DefaultSegmentTarget = 8L * 1024 * 1024;

    private readonly long _segmentTarget;

    public SegLogTranscriptStore()
    {
        _segmentTarget = DefaultSegmentTarget;
    }

    internal SegLogTranscriptStore(long segmentTarget)
    {
        if (segmentTarget <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(segmentTarget));
        }
        _segmentTarget = segmentTarget;
    }

    private const string SessionsDirName = "sessions";
    private const string IndexDbName = "index.db";

    /// <summary>
    /// Cumulative payload bytes of replaced suffix records, still resident in
    /// the segments as explicit garbage (no compaction is claimed; this is
    /// the retained-bytes reporting required for qualification).
    /// </summary>
    public long RetainedGarbageBytes { get; private set; }

    private string _storeRoot = string.Empty;
    private string _sessionsRoot = string.Empty;
    private SqliteConnection? _connection;
    private SqliteTransaction? _activeTx;

    public StoreDescriptor Describe() => new(
        StoreName,
        AckTier.OsFlush,
        "Segments: FileStream.Write + Flush() only (OS buffer cache, NO fsync). OsFlush is chosen, not forced: " +
        "the flush-to-disk option (FileStream.Flush(flushToDisk: true)) exists in .NET and is deliberately NOT used, " +
        "so no fsync claim is made. SQLite index: Microsoft.Data.Sqlite 10.0.14 with all defaults (journal_mode=DELETE " +
        "rollback journal, synchronous=FULL), so each index transaction commit fsyncs its own journal; the declared ack " +
        "tier is still OsFlush because the segment data stage is the weakest link and is never fsynced. Commit ordering " +
        "per mutation: segment bytes written and flushed FIRST, SQLite index transaction SECOND, and the mutation is " +
        "acknowledged only after the transaction commits. No power-loss claim; no fsync/WAL/tier overrides anywhere.",
        "Family 6 segmented binary log + explicit index. Per-session append-only immutable segments " +
        "(<storeRoot>/sessions/<hex(name)>/seg-NNNNNN.log, 8 MiB target, oversized-record dedicated-segment exception) " +
        "hold length-prefixed records ([int32 LE length][JSON payload]); index.db is the sole authority mapping " +
        "(session, ordinal) → (segment, offset, length, role, preview metadata) plus assignment rows/index and persisted " +
        "draft batches. Suffix edits append replacement records and replace the index suffix in one transaction — no " +
        "whole-session rewrite; superseded payload bytes remain as explicit garbage, reported as RetainedGarbageBytes, " +
        "and no compaction is claimed for initial qualification. Recovery at Open truncates the active segment's " +
        "unindexed tail, deletes wholly unreferenced segments beyond the active pointer, and sweeps unreferenced session " +
        "directories; referenced segment files are never truncated below their furthest indexed byte nor deleted, and a " +
        "missing or short referenced segment is corruption reported loudly. StreamDraft commits one batch per call; " +
        "FinalizeStream appends the frame then publishes the pointer row + count + marker/batch removal in one " +
        "transaction. DeleteSession commits the RequireSettled/CancelOpen assignment invariant atomically with catalog " +
        "and index removal, then removes the session directory best-effort (reclaimed at next Open otherwise). " +
        "Durability summary: the segment flush (no fsync — OsFlush declared) precedes every index transaction commit.");

    public void Open(string storeRoot, string attachmentRoot)
    {
        if (_connection is not null) throw new InvalidOperationException("Store already open");
        try
        {
            _storeRoot = storeRoot;
            _sessionsRoot = Path.Combine(storeRoot, SessionsDirName);
            Directory.CreateDirectory(_sessionsRoot);
            _connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(_storeRoot, IndexDbName),
                Pooling = false, // Dispose must release index.db (tests delete the store root)
            }.ToString());
            _connection.Open();
            foreach (var ddl in SchemaStatements)
            {
                Execute(ddl);
            }
            RecoverAndSweep();
        }
        catch
        {
            _connection?.Dispose();
            _connection = null;
            throw;
        }
    }

    // ---- reads ----

    public SessionMeta[] ListSessionsMeta()
    {
        using var reader = Query("SELECT name, count FROM session_ptr ORDER BY name").ExecuteReader();
        var metas = new List<SessionMeta>();
        while (reader.Read())
        {
            metas.Add(new SessionMeta(reader.GetString(0), reader.GetInt64(1)));
        }
        return [.. metas];
    }

    public MessageRecord[] OpenRecent(string sessionId, int count)
    {
        if (count <= 0) return [];
        var head = TrySession(sessionId);
        if (head is null) return [];
        var from = Math.Max(0, head.Count - count);
        return ReadWindow(sessionId, from, head.Count);
    }

    public MessageRecord[] PageOlder(string sessionId, long beforeOrdinal, int count)
    {
        if (count <= 0) return [];
        var head = TrySession(sessionId);
        if (head is null) return [];
        var to = Math.Clamp(beforeOrdinal, 0, head.Count);
        var from = Math.Max(0, to - count);
        return ReadWindow(sessionId, from, to);
    }

    public MessageRecord[] ReadRange(string sessionId, long fromOrdinal, long toOrdinalExclusive)
    {
        var head = TrySession(sessionId);
        if (head is null) return []; // absence is not an error for windowed reads
        if (fromOrdinal < 0 || toOrdinalExclusive < fromOrdinal || toOrdinalExclusive > head.Count)
        {
            throw new InvalidOperationException(
                $"Range [{fromOrdinal},{toOrdinalExclusive}) outside count={head.Count} in {sessionId}");
        }
        return ReadWindow(sessionId, fromOrdinal, toOrdinalExclusive);
    }

    public WindowPreview OpenRecentPreview(string sessionId, int count, int previewChars)
    {
        if (previewChars < 0)
        {
            throw new InvalidOperationException("previewChars must be non-negative");
        }
        var head = TrySession(sessionId);
        if (head is null) return new WindowPreview(sessionId, 0, []);
        var newest = Math.Min(count, head.Count);
        var from = head.Count - newest;
        var items = new List<MessagePreview>((int)newest);
        foreach (var row in MessageRowsBetween(sessionId, from, head.Count))
        {
            IReadOnlyList<PartPreview> parts;
            if (previewChars <= SegmentJson.PreviewCap)
            {
                // Persisted metadata path: no segment payload is decoded at all.
                parts = SegmentJson.DecodePartsMeta(row.Meta, previewChars);
            }
            else
            {
                // Bounded per-record fallback: fetches/decodes only this window's frames.
                parts = SegmentJson.DecodePartsMeta(row.Meta, previewChars, DecodeRow(sessionId, row).Parts);
            }
            items.Add(new MessagePreview(row.Ordinal, row.Role, parts));
        }
        return new WindowPreview(sessionId, from, items);
    }

    public string FetchToolResult(string sessionId, long ordinal, int partIndex)
    {
        var message = DecodeOrdinal(sessionId, ordinal); // targeted single-frame fetch
        if (partIndex < 0 || partIndex >= message.Parts.Count ||
            message.Parts[partIndex] is not ToolResultPart toolResult)
        {
            throw new InvalidOperationException($"{sessionId}@{ordinal}[{partIndex}] is not a tool result");
        }
        return toolResult.Content;
    }

    // ---- mutations ----

    public long AppendMessage(string sessionId, MessageRecord message)
    {
        var head = TrySession(sessionId);
        var (row, published) = AppendFrame(sessionId, head, message);
        Publish(sessionId, published, row, finalizeStream: null);
        return published.Count; // pre-append count == the new record ordinal
    }

    public void StreamDraft(string sessionId, string streamId, string chunk)
    {
        using var transaction = Begin();
        try
        {
            switch (TryDraftMarker(sessionId))
            {
                case null:
                    Execute("INSERT INTO draft_marker(session, stream, last_seq) VALUES ($s, $f, 1)",
                        ("$s", sessionId), ("$f", streamId));
                    Execute("INSERT INTO draft_batch(session, stream, seq, chunk) VALUES ($s, $f, 1, $c)",
                        ("$s", sessionId), ("$f", streamId), ("$c", chunk));
                    break;
                case { } active when active.Stream == streamId:
                    Execute("INSERT INTO draft_batch(session, stream, seq, chunk) VALUES ($s, $f, $q, $c)",
                        ("$s", sessionId), ("$f", streamId), ("$q", active.LastSequence + 1), ("$c", chunk));
                    Execute("UPDATE draft_marker SET last_seq = $q WHERE session = $s",
                        ("$q", active.LastSequence + 1), ("$s", sessionId));
                    break;
                case { } active:
                    throw new InvalidOperationException(
                        $"Session {sessionId} already has active draft {active.Stream}; only one draft per session");
            }
            Commit(transaction); // each StreamDraft call persists its own batch; never buffered until finalize
        }
        catch
        {
            Rollback(transaction);
            throw;
        }
    }

    public long FinalizeStream(string sessionId, string streamId, MessageRecord finalMessage)
    {
        var marker = TryDraftMarker(sessionId); // no file side effects before any check
        if (marker is not { } active)
        {
            throw new InvalidOperationException($"No active draft on session {sessionId}");
        }
        if (active.Stream != streamId)
        {
            throw new InvalidOperationException(
                $"No active draft {streamId} on session {sessionId} (active draft {active.Stream})");
        }
        var head = TrySession(sessionId); // a finalized stream may be the session's first message
        var (row, published) = AppendFrame(sessionId, head, finalMessage);
        Publish(sessionId, published, row, finalizeStream: streamId);
        return published.Count; // pre-append count == the new record ordinal
    }

    public long ReplaceSuffix(string sessionId, long fromOrdinal, IReadOnlyList<MessageRecord> newMessages)
    {
        var head = TrySession(sessionId)
            ?? throw new InvalidOperationException($"Unknown session {sessionId}");
        if (fromOrdinal < 0 || fromOrdinal > head.Count)
        {
            throw new InvalidOperationException(
                $"ReplaceSuffix from={fromOrdinal} outside count={head.Count} in {sessionId}");
        }
        var retained = RetainedPayloadBytes(sessionId, fromOrdinal);
        var rows = new List<MessageRow>(newMessages.Count);
        foreach (var message in newMessages)
        {
            var (row, published) = AppendFrame(sessionId, head, message);
            head = published; // subsequent records in this call continue from the new pointer
            rows.Add(row);
        }
        // One transaction replaces the index suffix; old payload bytes stay as garbage.
        RetainedGarbageBytes += retained;
        using (var transaction = Begin())
        {
            try
            {
                Execute("DELETE FROM messages WHERE session = $s AND ordinal >= $o", ("$s", sessionId), ("$o", fromOrdinal));
                for (var i = 0; i < rows.Count; i++)
                {
                    InsertPointerRow(sessionId, fromOrdinal + i, rows[i]);
                }
                Execute("UPDATE session_ptr SET count = $c, cur_seg = $g, cur_end = $e WHERE name = $n",
                    ("$c", fromOrdinal + rows.Count), ("$g", head!.CurSegment), ("$e", head.CurEnd), ("$n", sessionId));
                Commit(transaction);
            }
            catch
            {
                Rollback(transaction);
                throw;
            }
        }
        return fromOrdinal + rows.Count;
    }

    public void CreateAssignment(AssignmentRecord assignment)
    {
        using var transaction = Begin();
        try
        {
            Execute("INSERT INTO assignment(id, target, title, status, result) VALUES ($i, $t, $ti, $st, $r)",
                ("$i", assignment.AssignmentId), ("$t", assignment.TargetSessionId), ("$ti", assignment.Title),
                ("$st", (long)assignment.Status), ("$r", (object?)assignment.ResultText ?? DBNull.Value));
            Execute("INSERT INTO assign_index(target, id, status) VALUES ($t, $i, $st)",
                ("$t", assignment.TargetSessionId), ("$i", assignment.AssignmentId), ("$st", (long)assignment.Status));
            Commit(transaction);
        }
        catch (SqliteException)
        {
            Rollback(transaction);
            throw new InvalidOperationException($"Assignment {assignment.AssignmentId} already exists");
        }
        catch
        {
            Rollback(transaction);
            throw;
        }
    }

    public void SettleAssignment(SettleAssignmentRequest request)
    {
        using (var reader = Query("SELECT 1 FROM assignment WHERE id = $i", ("$i", request.AssignmentId)).ExecuteReader())
        {
            if (!reader.Read())
            {
                throw new InvalidOperationException($"Unknown assignment {request.AssignmentId}");
            }
        }
        using var transaction = Begin();
        try
        {
            Execute("UPDATE assignment SET status = $st, result = $r WHERE id = $i",
                ("$st", (long)AssignmentStatus.Settled), ("$r", (object?)request.ResultText ?? DBNull.Value),
                ("$i", request.AssignmentId));
            Execute("UPDATE assign_index SET status = $st WHERE id = $i",
                ("$st", (long)AssignmentStatus.Settled), ("$i", request.AssignmentId));
            Commit(transaction);
        }
        catch
        {
            Rollback(transaction);
            throw;
        }
    }

    public AssignmentRecord GetAssignment(string assignmentId)
    {
        using var reader = Query(
            "SELECT target, title, status, result FROM assignment WHERE id = $i", ("$i", assignmentId)).ExecuteReader();
        if (!reader.Read())
        {
            throw new InvalidOperationException($"Unknown assignment {assignmentId}");
        }
        return new AssignmentRecord(
            assignmentId,
            reader.GetString(0),
            reader.GetString(1),
            (AssignmentStatus)reader.GetInt64(2),
            reader.IsDBNull(3) ? null : reader.GetString(3));
    }

    public void DeleteSession(string sessionId, DeletePolicy policy)
    {
        if (TrySession(sessionId) is null)
        {
            throw new InvalidOperationException($"Unknown session {sessionId}");
        }
        using var transaction = Begin();
        try
        {
            var open = Count($"SELECT COUNT(*) FROM assign_index WHERE target = $t AND status = $open",
                ("$t", sessionId), ("$open", (long)AssignmentStatus.Open));
            if (policy == DeletePolicy.RequireSettled && open > 0)
            {
                throw new InvalidOperationException(
                    $"DeleteSession {sessionId}: {open} open assignment(s) violate the RequireSettled invariant");
            }
            if (policy == DeletePolicy.CancelOpen && open > 0)
            {
                Execute("UPDATE assignment SET status = $st WHERE target = $t AND status = $open",
                    ("$st", (long)AssignmentStatus.Cancelled), ("$t", sessionId), ("$open", (long)AssignmentStatus.Open));
            }
            Execute("DELETE FROM session_ptr WHERE name = $n", ("$n", sessionId));
            Execute("DELETE FROM messages WHERE session = $s", ("$s", sessionId));
            Execute("DELETE FROM assign_index WHERE target = $t", ("$t", sessionId));
            Execute("DELETE FROM draft_marker WHERE session = $s", ("$s", sessionId));
            Execute("DELETE FROM draft_batch WHERE session = $s", ("$s", sessionId));
            Commit(transaction);
        }
        catch
        {
            Rollback(transaction);
            throw;
        }
        try
        {
            Directory.Delete(SessionDir(sessionId), recursive: true); // best-effort; next Open sweeps unreferenced dirs
        }
        catch (IOException)
        {
            // reclaimed by the next Open pass; never served, never state
        }
    }

    public void Dispose()
    {
        _activeTx?.Dispose();
        _activeTx = null;
        _connection?.Dispose();
        _connection = null;
    }

    // ---- segment appends (data stage) ----

    private sealed record SessionPtr(long Count, long CurSegment, long CurEnd);
    private sealed record DraftState(string Stream, long LastSequence);
    private sealed record MessageRow(long Ordinal, long Segment, long Offset, long Length, string Role, string Meta);

    /// <summary>
    /// Appends one framed record after the last indexed byte of the active
    /// segment stream (never rewrites old bytes), rolls to a fresh segment
    /// when the record no longer fits the 8 MiB target, and gives an
    /// oversized frame its own dedicated segment (the next append rolls
    /// again). Returns the pointer facts the caller must publish; the caller
    /// decides success inside the index transaction, and any failure here
    /// leaves an unindexed tail that the next Open truncates.
    /// </summary>
    private (MessageRow Row, SessionPtr Head) AppendFrame(string sessionId, SessionPtr? head, MessageRecord message)
    {
        var frame = SegmentFraming.EncodeRecord(SegmentJson.EncodeMessage(message));
        if (frame.Length == SegmentFraming.LengthPrefixBytes)
        {
            throw new InvalidOperationException("Empty message payload is not encodable");
        }
        long segment, offset, advanceEnd;
        if (head is null || head.CurEnd == 0)
        {
            // Fresh active slot (new session, post-oversize roll).
            segment = head?.CurSegment ?? 0;
            offset = 0;
        }
        else if (head.CurEnd + frame.Length > _segmentTarget)
        {
            segment = head.CurSegment + 1;
            offset = 0;
        }
        else
        {
            segment = head.CurSegment;
            offset = head.CurEnd;
        }
        var oversized = frame.Length > _segmentTarget;
        if (oversized && head is not null && head.CurEnd != 0)
        {
            // Dedicated-segment exception; the assignment above already rolled:
            // same target segment, but also force the NEXT append to a new segment.
        }
        advanceEnd = offset + frame.Length;
        var dir = SessionDir(sessionId);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, SegmentFileName(segment));
        try
        {
            using var stream = new FileStream(
                path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
            stream.Seek(offset, SeekOrigin.Begin);
            stream.Write(frame);
            stream.Flush(); // data stage handed to the OS BEFORE the index transaction (OsFlush)
        }
        catch (IOException)
        {
            throw; // unindexed tail; the current Open instance keeps serving the indexed prefix
        }
        var advance = oversized
            ? new SessionPtr(head?.Count ?? 0, segment + 1, 0)
            : new SessionPtr(head?.Count ?? 0, segment, advanceEnd);
        return (new MessageRow(
            advance.Count, segment, offset + SegmentFraming.LengthPrefixBytes, frame.Length - SegmentFraming.LengthPrefixBytes,
            message.Role, SegmentJson.EncodePartsMeta(message.Parts)), advance);
    }

    /// <summary>
    /// Publishes an acknowledged record in ONE index transaction: the pointer
    /// row, the count, and (finalize) marker/batch deletion. The advanced
    /// pointer in <paramref name="published"/> is authoritative from here.
    /// Insert-vs-update is decided on the pointer row's EXISTENCE (queried
    /// inside the transaction), never on count: ReplaceSuffix(…, 0, …) gives
    /// an <i>existing</i> row at count 0, and the next append/finalize must
    /// UPDATE it instead of INSERTing a duplicate name.
    /// </summary>
    private void Publish(string sessionId, SessionPtr published, MessageRow row, string? finalizeStream)
    {
        using var transaction = Begin();
        try
        {
            InsertPointerRow(sessionId, row.Ordinal, row);
            var exists = Query("SELECT 1 FROM session_ptr WHERE name = $n", ("$n", sessionId))
                .ExecuteReader().Read();
            if (!exists)
            {
                // The pointer row does not exist yet (session's first record).
                Execute("INSERT INTO session_ptr(name, count, cur_seg, cur_end) VALUES ($n, 1, $g, $e)",
                    ("$n", sessionId), ("$g", published.CurSegment), ("$e", published.CurEnd));
            }
            else
            {
                Execute("UPDATE session_ptr SET count = $c, cur_seg = $g, cur_end = $e WHERE name = $n",
                    ("$c", published.Count + 1), ("$g", published.CurSegment), ("$e", published.CurEnd), ("$n", sessionId));
            }
            if (finalizeStream is not null)
            {
                Execute("DELETE FROM draft_marker WHERE session = $s", ("$s", sessionId));
                Execute("DELETE FROM draft_batch WHERE session = $s AND stream = $f",
                    ("$s", sessionId), ("$f", finalizeStream));
            }
            Commit(transaction);
        }
        catch
        {
            Rollback(transaction);
            throw;
        }
    }

    // ---- recovery ----

    private void RecoverAndSweep()
    {
        var referencedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var heads = new List<(string Session, long CurSegment, long CurEnd)>();
        using (var reader = Query("SELECT name, cur_seg, cur_end FROM session_ptr ORDER BY name").ExecuteReader())
        {
            while (reader.Read())
            {
                heads.Add((reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2)));
            }
        }
        foreach (var (session, curSegment, curEnd) in heads)
        {
            var dir = SessionDir(session);
            if (!Directory.Exists(dir))
            {
                throw new InvalidOperationException($"Referenced session directory is missing for {session}");
            }
            // Furthest indexed byte per referenced segment (authority: the index).
            var referenced = new List<(long Segment, long Furthest)>();
            using (var reader = Query(
                       "SELECT seg, MAX(offset + length) FROM messages WHERE session = $s GROUP BY seg",
                       ("$s", session)).ExecuteReader())
            {
                while (reader.Read())
                {
                    referenced.Add((reader.GetInt64(0), reader.GetInt64(1)));
                    if (reader.GetInt64(0) > curSegment)
                    {
                        throw new InvalidOperationException(
                            $"Corrupt pointer for {session}: message references segment {reader.GetInt64(0)} beyond active {curSegment}");
                    }
                }
            }
            foreach (var (segment, furthest) in referenced)
            {
                var path = Path.Combine(dir, SegmentFileName(segment));
                if (!File.Exists(path))
                {
                    throw new InvalidOperationException(
                        $"Referenced segment is missing for {session} ({SegmentFileName(segment)})");
                }
                using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
                if (stream.Length < furthest)
                {
                    throw new InvalidOperationException(
                        $"Segment shorter than index ({stream.Length} < {furthest}) for {session}/{SegmentFileName(segment)}");
                }
                if (stream.Length > furthest)
                {
                    // Unindexed bytes beyond the furthest indexed byte (an
                    // uncommitted data stage is never served). Referenced
                    // payload bytes BELOW the furthest end are never touched.
                    stream.SetLength(furthest);
                    stream.Flush();
                }
            }
            // The ACTIVE slot is enforced by cur_end directly, even when the
            // count is zero (no references): a pointer row can legitimately
            // exist at count 0 (after ReplaceSuffix(…, 0, …)), and a fresh
            // oversize slot holds cur_end = 0. If an unacknowledged data stage
            // left unindexed tail bytes there, they are truncated to the
            // authoritative cur_end; a file shorter than cur_end is corruption.
            var activePath = Path.Combine(dir, SegmentFileName(curSegment));
            if (File.Exists(activePath))
            {
                using var activeStream = new FileStream(activePath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
                if (activeStream.Length < curEnd)
                {
                    throw new InvalidOperationException(
                        $"Active segment shorter than the append position ({activeStream.Length} < {curEnd}) " +
                        $"for {session}/{SegmentFileName(curSegment)}");
                }
                if (activeStream.Length > curEnd)
                {
                    activeStream.SetLength(curEnd);
                    activeStream.Flush();
                }
            }
            // Wholly unreferenced segments beyond the active pointer can never
            // be pointed at again (the pointer advances only with an
            // acknowledged index transaction): safely deletable.
            foreach (var path in Directory.EnumerateFiles(dir, "seg-*.log"))
            {
                if (SegmentIdOf(Path.GetFileName(path)) > curSegment)
                {
                    File.Delete(path);
                }
            }
            referencedDirs.Add(SegmentDirs.DirName(session));
        }
        foreach (var dir in Directory.EnumerateDirectories(_sessionsRoot))
        {
            if (!referencedDirs.Contains(Path.GetFileName(dir)))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    // ---- internals ----

    private SessionPtr? TrySession(string sessionId)
    {
        using var reader = Query(
            "SELECT count, cur_seg, cur_end FROM session_ptr WHERE name = $n", ("$n", sessionId)).ExecuteReader();
        if (!reader.Read()) return null;
        return new SessionPtr(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
    }

    private static string SegmentFileName(long segmentId) => $"seg-{segmentId:D6}.log";

    private static long SegmentIdOf(string fileName) => long.Parse(
        fileName.AsSpan(4, fileName.Length - 8), System.Globalization.CultureInfo.InvariantCulture);

    private string SessionDir(string sessionId) =>
        Path.Combine(_sessionsRoot, SegmentDirs.DirName(sessionId));

    private long RetainedPayloadBytes(string sessionId, long fromOrdinal)
    {
        using var reader = Query(
            "SELECT COALESCE(SUM(length), 0) FROM messages WHERE session = $s AND ordinal >= $o",
            ("$s", sessionId), ("$o", fromOrdinal)).ExecuteReader();
        reader.Read();
        return reader.GetInt64(0);
    }

    private void InsertPointerRow(string sessionId, long ordinal, MessageRow row)
    {
        Execute(
            "INSERT INTO messages(session, ordinal, seg, offset, length, role, meta) VALUES ($s, $o, $g, $f, $l, $r, $m)",
            ("$s", sessionId), ("$o", ordinal), ("$g", row.Segment), ("$f", row.Offset),
            ("$l", row.Length), ("$r", row.Role), ("$m", row.Meta));
    }

    private List<MessageRow> MessageRowsBetween(string sessionId, long from, long to)
    {
        var rows = new List<MessageRow>((int)Math.Max(0, to - from));
        if (from >= to) return rows;
        using var reader = Query(
            "SELECT ordinal, seg, offset, length, role, meta FROM messages " +
            "WHERE session = $s AND ordinal >= $f AND ordinal < $t ORDER BY ordinal",
            ("$s", sessionId), ("$f", from), ("$t", to)).ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new MessageRow(
                reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2),
                reader.GetInt64(3), reader.GetString(4), reader.GetString(5)));
        }
        return rows;
    }

    private DraftState? TryDraftMarker(string sessionId)
    {
        using var reader = Query(
            "SELECT stream, last_seq FROM draft_marker WHERE session = $s", ("$s", sessionId)).ExecuteReader();
        return reader.Read() ? new DraftState(reader.GetString(0), reader.GetInt64(1)) : null;
    }

    private MessageRecord DecodeRow(string sessionId, MessageRow row)
    {
        var record = new byte[SegmentFraming.LengthPrefixBytes + row.Length];
        using (var stream = new FileStream(
                   Path.Combine(SessionDir(sessionId), SegmentFileName(row.Segment)),
                   FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            stream.Seek(row.Offset - SegmentFraming.LengthPrefixBytes, SeekOrigin.Begin);
            stream.ReadExactly(record);
        }
        var payloadLength = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(record);
        if (payloadLength != row.Length)
        {
            throw new InvalidOperationException(
                $"Framing mismatch for {sessionId}@{row.Ordinal} (frame says {payloadLength}, index says {row.Length})");
        }
        return SegmentJson.DecodeMessage(new Span<byte>(record, SegmentFraming.LengthPrefixBytes, checked((int)row.Length)));
    }

    private MessageRecord DecodeOrdinal(string sessionId, long ordinal)
    {
        using var reader = Query(
            "SELECT ordinal, seg, offset, length, role, meta FROM messages WHERE session = $s AND ordinal = $o",
            ("$s", sessionId), ("$o", ordinal)).ExecuteReader();
        if (!reader.Read())
        {
            throw new InvalidOperationException($"Unknown message {sessionId}@{ordinal}");
        }
        return DecodeRow(sessionId, new MessageRow(
            reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2),
            reader.GetInt64(3), reader.GetString(4), reader.GetString(5)));
    }

    private MessageRecord[] ReadWindow(string sessionId, long from, long to)
    {
        var rows = MessageRowsBetween(sessionId, from, to);
        if (rows.Count == 0) return [];
        var messages = new List<MessageRecord>(rows.Count);
        foreach (var row in rows)
        {
            messages.Add(DecodeRow(sessionId, row));
        }
        return [.. messages];
    }

    private static readonly string[] SchemaStatements =
    [
        "CREATE TABLE IF NOT EXISTS session_ptr (name TEXT PRIMARY KEY, count INTEGER NOT NULL, cur_seg INTEGER NOT NULL, cur_end INTEGER NOT NULL)",
        "CREATE TABLE IF NOT EXISTS messages (session TEXT NOT NULL, ordinal INTEGER NOT NULL, seg INTEGER NOT NULL, offset INTEGER NOT NULL, length INTEGER NOT NULL, role TEXT NOT NULL, meta TEXT NOT NULL, PRIMARY KEY (session, ordinal))",
        "CREATE TABLE IF NOT EXISTS assignment (id TEXT PRIMARY KEY, target TEXT NOT NULL, title TEXT NOT NULL, status INTEGER NOT NULL, result TEXT)",
        "CREATE TABLE IF NOT EXISTS assign_index (target TEXT NOT NULL, id TEXT NOT NULL, status INTEGER NOT NULL, PRIMARY KEY (target, id))",
        "CREATE TABLE IF NOT EXISTS draft_marker (session TEXT PRIMARY KEY, stream TEXT NOT NULL, last_seq INTEGER NOT NULL)",
        "CREATE TABLE IF NOT EXISTS draft_batch (session TEXT NOT NULL, stream TEXT NOT NULL, seq INTEGER NOT NULL, chunk TEXT NOT NULL, PRIMARY KEY (session, stream, seq))",
        "CREATE INDEX IF NOT EXISTS assign_target_index ON assign_index(target)",
    ];

    // ---- low-level SQLite helpers ----

    private SqliteTransaction Begin()
    {
        var transaction = _connection!.BeginTransaction();
        _activeTx = transaction;
        return transaction;
    }

    private void Commit(SqliteTransaction transaction)
    {
        transaction.Commit();
        _activeTx = null;
    }

    private void Rollback(SqliteTransaction transaction)
    {
        _activeTx = null;
        transaction.Rollback();
    }

    private SqliteCommand Query(string sql, params (string Name, object Value)[] binds)
    {
        var command = _connection!.CreateCommand();
        command.CommandText = sql;
        if (_activeTx is { } transaction)
        {
            command.Transaction = transaction; // Microsoft.Data.Sqlite requires explicit association
        }
        foreach (var (name, value) in binds)
        {
            command.Parameters.AddWithValue(name, value);
        }
        return command;
    }

    private void Execute(string sql, params (string Name, object Value)[] binds)
    {
        using var command = Query(sql, binds);
        command.ExecuteNonQuery();
    }

    private long Count(string sql, params (string Name, object Value)[] binds)
    {
        using var command = Query(sql, binds);
        return Convert.ToInt64(command.ExecuteScalar() ?? 0L, System.Globalization.CultureInfo.InvariantCulture);
    }
}