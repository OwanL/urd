using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Urd.TranscriptHarness.Contracts;

namespace Urd.TranscriptHarness.Core;

/// <summary>
/// Store-host server: reads one JSON-line request per line from stdin, writes
/// one JSON-line response per request to stdout, plus a "ready" event at
/// startup and a "fatal" event instead of dying silently. Stdout stays clean
/// for the protocol; unexpected errors surface as fatal, never as log noise.
///
/// Every successful response carries "t" (store-method elapsed microseconds)
/// and, when the store implements IStoreStatistics, "a" (managed allocation
/// delta). Both are measured strictly around the store method call: request
/// JSON parsing and response JSON serialization are excluded, so the parent
/// can separate host-method cost from its own RPC-side latency.
/// </summary>
public sealed class HostServer(ITranscriptStore store, IStoreStatistics? statistics, TextReader input, TextWriter output)
{
    private long _opMicros;
    private long? _opAllocBytes;

    public void Serve()
    {
        try
        {
            EmitEvent(Protocol.Ready, "{}");
            string? line;
            while ((line = input.ReadLine()) is not null)
            {
                if (line.Length == 0) continue;
                var closed = Handle(JsonDocument.Parse(line).RootElement);
                if (closed) break;
            }
        }
        catch (Exception ex)
        {
            EmitEvent(Protocol.Fatal, $"{{\"message\":{JsonSerializer.Serialize(ex.Message)}}}");
            throw;
        }
    }

    private bool Handle(JsonElement root)
    {
        var id = root.GetProperty("i").GetInt64();
        try
        {
            var op = root.GetProperty("op").GetString()!;
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                Dispatch(writer, op, root.GetProperty("p"));
            }
            var result = JsonDocument.Parse(buffer.ToArray()).RootElement.Clone();
            EmitResponse(id, null, result, _opMicros, _opAllocBytes);
            return op == ProtocolOps.Close;
        }
        catch (Exception ex)
        {
            EmitResponse(id, ex.Message, null, null, null);
            return false;
        }
    }

    private void Dispatch(Utf8JsonWriter w, string op, JsonElement p)
    {
        _opMicros = 0;
        _opAllocBytes = null;
        switch (op)
        {
            case ProtocolOps.Open:
            {
                var storeRoot = p.GetProperty("root").GetString()!;
                var attachmentRoot = p.GetProperty("attachmentRoot").GetString()!;
                StoreTimed(() => store.Open(storeRoot, attachmentRoot));
                w.WriteStartObject(); w.WriteEndObject();
                break;
            }
            case ProtocolOps.Describe:
            {
                var descriptor = StoreTimed(store.Describe);
                w.WriteStartObject();
                w.WriteString("storeName", descriptor.StoreName);
                w.WriteString("ackTier", descriptor.AckTier.ToString());
                w.WriteString("settingsNote", descriptor.SettingsNote);
                w.WriteString("notes", descriptor.Notes);
                w.WriteEndObject();
                break;
            }
            case ProtocolOps.ListSessions:
            {
                var metas = StoreTimed(store.ListSessionsMeta);
                HarnessJson.WriteSessionMetas(w, metas);
                break;
            }
            case ProtocolOps.OpenRecent:
            {
                var sessionId = p.GetProperty("sessionId").GetString()!;
                var count = p.GetProperty("count").GetInt32();
                HarnessJson.WriteMessages(w, StoreTimed(() => store.OpenRecent(sessionId, count)));
                break;
            }
            case ProtocolOps.PageOlder:
            {
                var sessionId = p.GetProperty("sessionId").GetString()!;
                var before = p.GetProperty("before").GetInt64();
                var count = p.GetProperty("count").GetInt32();
                HarnessJson.WriteMessages(w, StoreTimed(() => store.PageOlder(sessionId, before, count)));
                break;
            }
            case ProtocolOps.ReadRange:
            {
                var sessionId = p.GetProperty("sessionId").GetString()!;
                var from = p.GetProperty("from").GetInt64();
                var to = p.GetProperty("to").GetInt64();
                HarnessJson.WriteMessages(w, StoreTimed(() => store.ReadRange(sessionId, from, to)));
                break;
            }
            case ProtocolOps.WindowPreview:
            {
                var sessionId = p.GetProperty("sessionId").GetString()!;
                var count = p.GetProperty("count").GetInt32();
                var previewChars = p.GetProperty("previewChars").GetInt32();
                HarnessJson.WriteWindowPreview(w, StoreTimed(() => store.OpenRecentPreview(sessionId, count, previewChars)));
                break;
            }
            case ProtocolOps.FetchToolResult:
            {
                var sessionId = p.GetProperty("sessionId").GetString()!;
                var ordinal = p.GetProperty("ordinal").GetInt64();
                var partIndex = p.GetProperty("partIndex").GetInt32();
                var content = StoreTimed(() => store.FetchToolResult(sessionId, ordinal, partIndex));
                w.WriteStartObject();
                w.WriteString("content", content);
                w.WriteEndObject();
                break;
            }
            case ProtocolOps.Append:
            {
                var sessionId = p.GetProperty("sessionId").GetString()!;
                var message = HarnessJson.ReadMessage(p.GetProperty("message"));
                var ordinal = StoreTimed(() => store.AppendMessage(sessionId, message));
                w.WriteStartObject();
                w.WriteNumber("ordinal", ordinal);
                w.WriteEndObject();
                break;
            }
            case ProtocolOps.StreamDraft:
            {
                var sessionId = p.GetProperty("sessionId").GetString()!;
                var streamId = p.GetProperty("streamId").GetString()!;
                var chunk = p.GetProperty("chunk").GetString()!;
                StoreTimed(() => store.StreamDraft(sessionId, streamId, chunk));
                w.WriteStartObject(); w.WriteEndObject();
                break;
            }
            case ProtocolOps.Finalize:
            {
                var sessionId = p.GetProperty("sessionId").GetString()!;
                var streamId = p.GetProperty("streamId").GetString()!;
                var message = HarnessJson.ReadMessage(p.GetProperty("message"));
                var ordinal = StoreTimed(() => store.FinalizeStream(sessionId, streamId, message));
                w.WriteStartObject();
                w.WriteNumber("ordinal", ordinal);
                w.WriteEndObject();
                break;
            }
            case ProtocolOps.ReplaceSuffix:
            {
                var sessionId = p.GetProperty("sessionId").GetString()!;
                var from = p.GetProperty("from").GetInt64();
                var messages = HarnessJson.ReadMessages(p.GetProperty("messages"));
                var count = StoreTimed(() => store.ReplaceSuffix(sessionId, from, messages));
                w.WriteStartObject();
                w.WriteNumber("count", count);
                w.WriteEndObject();
                break;
            }
            case ProtocolOps.CreateAssignment:
            {
                var assignment = HarnessJson.ReadAssignment(p.GetProperty("assignment"));
                StoreTimed(() => store.CreateAssignment(assignment));
                w.WriteStartObject(); w.WriteEndObject();
                break;
            }
            case ProtocolOps.Settle:
            {
                var request = new SettleAssignmentRequest(
                    p.GetProperty("assignmentId").GetString()!, p.GetProperty("resultText").GetString()!);
                StoreTimed(() => store.SettleAssignment(request));
                w.WriteStartObject(); w.WriteEndObject();
                break;
            }
            case ProtocolOps.GetAssignment:
            {
                var assignmentId = p.GetProperty("assignmentId").GetString()!;
                HarnessJson.WriteAssignment(w, StoreTimed(() => store.GetAssignment(assignmentId)));
                break;
            }
            case ProtocolOps.DeleteSession:
            {
                var sessionId = p.GetProperty("sessionId").GetString()!;
                var policy = Enum.Parse<DeletePolicy>(p.GetProperty("policy").GetString()!);
                StoreTimed(() => store.DeleteSession(sessionId, policy));
                w.WriteStartObject(); w.WriteEndObject();
                break;
            }
            case ProtocolOps.Statistics:
                if (statistics == null) throw new InvalidOperationException("Store does not implement IStoreStatistics");
                var stats = StoreTimed(statistics.Sample);
                w.WriteStartObject();
                w.WriteNumber("managedAllocatedBytes", stats.ManagedAllocatedBytes);
                w.WriteEndObject();
                break;
            case ProtocolOps.Close:
                w.WriteStartObject(); w.WriteEndObject();
                break;
            default:
                throw new InvalidOperationException($"Unknown protocol op '{op}'");
        }
    }

    private void StoreTimed(Action call)
    {
        StoreTimed<object?>(() =>
        {
            call();
            return null;
        });
    }

    private T StoreTimed<T>(Func<T> call)
    {
        long beforeAlloc = 0;
        if (statistics is not null) beforeAlloc = GC.GetTotalAllocatedBytes(precise: true);
        var stopwatch = Stopwatch.StartNew();
        var result = call();
        stopwatch.Stop();
        _opMicros = stopwatch.Elapsed.Ticks * 1_000_000L / TimeSpan.TicksPerSecond;
        if (statistics is not null) _opAllocBytes = GC.GetTotalAllocatedBytes(precise: true) - beforeAlloc;
        return result;
    }

    private void EmitResponse(long id, string? error, JsonElement? result, long? micros, long? allocBytes)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("i", id);
            if (error != null) writer.WriteString("e", error); else writer.WriteNull("e");
            if (micros is { } t) writer.WriteNumber("t", t); else writer.WriteNull("t");
            if (allocBytes is { } a) writer.WriteNumber("a", a); else writer.WriteNull("a");
            if (result is { } r) writer.WritePropertyName("r"); else writer.WriteNull("r");
            if (result is { } value) value.WriteTo(writer);
            writer.WriteEndObject();
        }
        output.WriteLine(Encoding.UTF8.GetString(buffer.ToArray()));
        output.Flush();
    }

    private void EmitEvent(string kind, string payloadJson)
    {
        output.WriteLine($"{{\"ev\":\"{kind}\",\"p\":{payloadJson}}}");
        output.Flush();
    }
}