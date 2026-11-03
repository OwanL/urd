using System.Diagnostics;
using Urd.TranscriptHarness.Contracts;

namespace Urd.TranscriptHarness.Core;

// Trace executor: replays a generated trace against a client (in-process
// reference or IPC-based store), keeping the oracle model in step and verifying
// at checkpoints. The runner owns all expectations; the store host process is
// the only engine that is measured.

public sealed record RunSummary(
    string StoreName,
    StoreDescriptor Descriptor,
    int OpCount,
    Dictionary<string, NamedStats> ReadLatency,
    Dictionary<string, NamedStats> MutationLatency,
    long Checkpoints,
    long? ManagedAllocatedBytesBefore,
    long? ManagedAllocatedBytesAfter,
    long PrivateBytesPeak,
    long WorkingSetPeak,
    long VerifyPrivateBytesPeak,
    long VerifyWorkingSetPeak,
    long StoreFootprintBytes,
    int StoreFootprintFiles,
    string[] Warnings);

public sealed class TraceExecutor(ITranscriptStore client, OracleModel model)
{
    private readonly Dictionary<string, List<double>> _readLatency = [];
    private readonly Dictionary<string, List<double>> _mutationLatency = [];
    private long _checkpoints;

    public void Replay(IReadOnlyList<TraceOp> ops)
    {
        for (var i = 0; i < ops.Count; i++)
        {
            Execute(ops[i], i);
        }
    }

    public void Execute(TraceOp op, int index)
    {
        switch (op)
        {
            case TraceAppend a:
            {
                var expected = ModelOps.AppendMessage(model, a.SessionId, a.Message);
                var actual = Timed(TraceOps.Append, _mutationLatency, () => client.AppendMessage(a.SessionId, a.Message));
                if (actual != expected)
                {
                    throw new HarnessVerificationError(
                        $"op {index}: Append in {a.SessionId}: store ordinal {actual} != expected {expected}");
                }
                break;
            }

            case TraceStreamDraft d:
                Timed(TraceOps.StreamDraft, _mutationLatency, () =>
                {
                    client.StreamDraft(d.SessionId, d.StreamId, d.Chunk);
                    return 0d;
                });
                model.ActiveDrafts.Add(d.StreamId);
                break;

            case TraceFinalize f:
            {
                var expected = ModelOps.FinalizeStream(model, f.SessionId, f.StreamId, f.FinalMessage);
                var actual = Timed(TraceOps.Finalize, _mutationLatency, () => client.FinalizeStream(f.SessionId, f.StreamId, f.FinalMessage));
                if (actual != expected)
                {
                    throw new HarnessVerificationError(
                        $"op {index}: FinalizeStream {f.StreamId} in {f.SessionId}: ordinal {actual} != expected {expected}");
                }
                break;
            }

            case TraceReplaceSuffix r:
            {
                var expected = ModelOps.ReplaceSuffix(model, r.SessionId, r.FromOrdinal, r.NewMessages);
                var actual = Timed(TraceOps.ReplaceSuffix, _mutationLatency, () => client.ReplaceSuffix(r.SessionId, r.FromOrdinal, r.NewMessages));
                if (actual != expected)
                {
                    throw new HarnessVerificationError(
                        $"op {index}: ReplaceSuffix {r.FromOrdinal} in {r.SessionId}: count {actual} != expected {expected}");
                }
                break;
            }

            case TraceDeleteSession d:
                Timed(TraceOps.DeleteSession, _mutationLatency, () =>
                {
                    client.DeleteSession(d.SessionId, d.Policy);
                    return 0d;
                });
                ModelOps.DeleteSession(model, d.SessionId, d.Policy);
                break;

            case TraceListSessions:
            {
                var metas = Timed(TraceOps.ListSessions, _readLatency, client.ListSessionsMeta);
                Oracle.VerifySessionIndex(metas, model);
                break;
            }

            case TraceOpenRecent o:
            {
                var actual = Timed(TraceOps.OpenRecent, _readLatency, () => client.OpenRecent(o.SessionId, o.Count));
                var preCount = RequireCount(o.SessionId);
                var from = Math.Max(0, preCount - o.Count);
                Oracle.CompareWindow(actual, ModelOps.WindowOf(model, o.SessionId, from, null),
                    $"openRecent({o.SessionId}, n={o.Count})");
                break;
            }

            case TracePageOlder o:
            {
                var actual = Timed(TraceOps.PageOlder, _readLatency, () => client.PageOlder(o.SessionId, o.Before, o.Count));
                var preCount = RequireCount(o.SessionId);
                var from = Math.Clamp(o.Before - o.Count, 0, preCount);
                Oracle.CompareWindow(actual, ModelOps.WindowOf(model, o.SessionId, from, Math.Min(o.Before, preCount)),
                    $"pageOlder({o.SessionId}, before={o.Before}, n={o.Count})");
                break;
            }

            case TraceReadRange o:
            {
                var actual = Timed(TraceOps.ReadRange, _readLatency, () => client.ReadRange(o.SessionId, o.From, o.To));
                Oracle.CompareWindow(actual, ModelOps.WindowOf(model, o.SessionId, o.From, o.To),
                    $"readRange({o.SessionId}, [{o.From},{o.To}))");
                break;
            }

            case TraceWindowPreview o:
            {
                var actual = Timed(TraceOps.WindowPreview, _readLatency,
                    () => client.OpenRecentPreview(o.SessionId, o.Count, o.PreviewChars));
                if (!model.Sessions.TryGetValue(o.SessionId, out var expectedList))
                {
                    throw new HarnessVerificationError($"op {index}: WindowPreview of absent session {o.SessionId}");
                }
                var expectedFrom = Math.Max(0, expectedList.Count - o.Count);
                Oracle.ComparePreviewWindow(actual, expectedList, expectedFrom, o.PreviewChars, o.SessionId);
                break;
            }

            case TraceFetchToolResult o:
            {
                var content = Timed(TraceOps.FetchToolResult, _readLatency, () => client.FetchToolResult(o.SessionId, o.Ordinal, o.PartIndex));
                var session = model.Sessions.TryGetValue(o.SessionId, out var list)
                    ? list
                    : throw new HarnessVerificationError($"op {index}: FetchToolResult of absent session {o.SessionId}");
                var expectedMessage = session.ElementAtOrDefault((int)o.Ordinal)
                    ?? throw new HarnessVerificationError($"op {index}: FetchToolResult ordinal {o.Ordinal} beyond expected history");
                if (o.PartIndex >= expectedMessage.Parts.Count ||
                    expectedMessage.Parts[o.PartIndex] is not ToolResultPart expectedResult ||
                    !string.Equals(content, expectedResult.Content, StringComparison.Ordinal))
                {
                    throw new HarnessVerificationError(
                        $"op {index}: FetchToolResult {o.SessionId}@{o.Ordinal}[{o.PartIndex}] content mismatch");
                }
                break;
            }

            case TraceCreateAssignment c:
            {
                var assignment = new AssignmentRecord(c.AssignmentId, c.TargetSessionId, c.Title, AssignmentStatus.Open, null);
                Timed(TraceOps.CreateAssignment, _mutationLatency, () =>
                {
                    client.CreateAssignment(assignment);
                    return 0d;
                });
                ModelOps.CreateAssignment(model, assignment);
                break;
            }

            case TraceSettle s:
            {
                Timed(TraceOps.Settle, _mutationLatency, () =>
                {
                    client.SettleAssignment(new SettleAssignmentRequest(s.AssignmentId, s.ResultText));
                    return 0d;
                });
                ModelOps.SettleAssignment(model, new SettleAssignmentRequest(s.AssignmentId, s.ResultText));
                break;
            }

            case TraceCheckpoint:
                Oracle.VerifyAll(client, model);
                _checkpoints++;
                break;

            case TraceCrashMarker:
                // Recorded only in this milestone; kill acceptance activates with
                // the first real persistent adapter (see README, deferred items).
                break;

            default:
                throw new HarnessError($"Unknown trace op {op.GetType().Name}");
        }
    }

    private long RequireCount(string sessionId) =>
        model.Count(sessionId) > 0
            ? model.Count(sessionId)
            : throw new HarnessVerificationError($"Read op for session {sessionId} with no expected content");

    private static T Timed<T>(string op, Dictionary<string, List<double>> table, Func<T> work)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = work();
        stopwatch.Stop();
        if (!table.TryGetValue(op, out var list))
        {
            list = [];
            table[op] = list;
        }
        list.Add(stopwatch.Elapsed.TotalMilliseconds);
        return result;
    }

    public Dictionary<string, NamedStats> ReadLatency =>
        _readLatency.ToDictionary(kv => kv.Key, kv => Stats.Summarize(kv.Value));

    public Dictionary<string, NamedStats> MutationLatency =>
        _mutationLatency.ToDictionary(kv => kv.Key, kv => Stats.Summarize(kv.Value));

    public long Checkpoints => _checkpoints;
}