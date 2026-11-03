using System.Text;
using System.Text.Json;
using Urd.TranscriptHarness.Contracts;

namespace Urd.TranscriptHarness.Core;

/// <summary>
/// Store-host server: reads one JSON-line request per line from stdin, writes
/// one JSON-line response per request to stdout, plus a "ready" event at
/// startup and a "fatal" event instead of dying silently. Stdout stays clean
/// for the protocol; unexpected errors surface as fatal, never as log noise.
/// </summary>
public sealed class HostServer(ITranscriptStore store, IStoreStatistics? statistics, TextReader input, TextWriter output)
{
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
            EmitEvent(Protocol.Fatal, $"{{\"message\":{System.Text.Json.JsonSerializer.Serialize(ex.Message)}}}");
            throw;
        }
    }

    private bool Handle(JsonElement root)
    {
        var id = root.GetProperty("i").GetInt64();
        try
        {
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                Dispatch(writer, root.GetProperty("op").GetString()!, root.GetProperty("p"));
            }
            var result = JsonDocument.Parse(buffer.ToArray()).RootElement.Clone();
            EmitResponse(id, null, result);
            return root.GetProperty("op").GetString() == ProtocolOps.Close;
        }
        catch (Exception ex)
        {
            EmitResponse(id, ex.Message, null);
            return false;
        }
    }

    private void Dispatch(Utf8JsonWriter w, string op, JsonElement p)
    {
        switch (op)
        {
            case ProtocolOps.Open:
                store.Open(p.GetProperty("root").GetString()!, p.GetProperty("attachmentRoot").GetString()!);
                w.WriteStartObject(); w.WriteEndObject();
                break;
            case ProtocolOps.Describe:
                var descriptor = store.Describe();
                w.WriteStartObject();
                w.WriteString("storeName", descriptor.StoreName);
                w.WriteString("ackTier", descriptor.AckTier.ToString());
                w.WriteString("settingsNote", descriptor.SettingsNote);
                w.WriteString("notes", descriptor.Notes);
                w.WriteEndObject();
                break;
            case ProtocolOps.ListSessions:
                HarnessJson.WriteSessionMetas(w, store.ListSessionsMeta());
                break;
            case ProtocolOps.OpenRecent:
                HarnessJson.WriteMessages(w, store.OpenRecent(p.GetProperty("sessionId").GetString()!, p.GetProperty("count").GetInt32()));
                break;
            case ProtocolOps.PageOlder:
                HarnessJson.WriteMessages(w, store.PageOlder(
                    p.GetProperty("sessionId").GetString()!, p.GetProperty("before").GetInt64(), p.GetProperty("count").GetInt32()));
                break;
            case ProtocolOps.ReadRange:
                HarnessJson.WriteMessages(w, store.ReadRange(
                    p.GetProperty("sessionId").GetString()!, p.GetProperty("from").GetInt64(), p.GetProperty("to").GetInt64()));
                break;
            case ProtocolOps.WindowPreview:
                HarnessJson.WriteWindowPreview(w, store.OpenRecentPreview(
                    p.GetProperty("sessionId").GetString()!, p.GetProperty("count").GetInt32(), p.GetProperty("previewChars").GetInt32()));
                break;
            case ProtocolOps.FetchToolResult:
                w.WriteStartObject();
                w.WriteString("content", store.FetchToolResult(
                    p.GetProperty("sessionId").GetString()!, p.GetProperty("ordinal").GetInt64(), p.GetProperty("partIndex").GetInt32()));
                w.WriteEndObject();
                break;
            case ProtocolOps.Append:
                w.WriteStartObject();
                w.WriteNumber("ordinal", store.AppendMessage(
                    p.GetProperty("sessionId").GetString()!, HarnessJson.ReadMessage(p.GetProperty("message"))));
                w.WriteEndObject();
                break;
            case ProtocolOps.StreamDraft:
                store.StreamDraft(p.GetProperty("sessionId").GetString()!, p.GetProperty("streamId").GetString()!, p.GetProperty("chunk").GetString()!);
                w.WriteStartObject(); w.WriteEndObject();
                break;
            case ProtocolOps.Finalize:
                w.WriteStartObject();
                w.WriteNumber("ordinal", store.FinalizeStream(
                    p.GetProperty("sessionId").GetString()!, p.GetProperty("streamId").GetString()!, HarnessJson.ReadMessage(p.GetProperty("message"))));
                w.WriteEndObject();
                break;
            case ProtocolOps.ReplaceSuffix:
                w.WriteStartObject();
                w.WriteNumber("count", store.ReplaceSuffix(
                    p.GetProperty("sessionId").GetString()!,
                    p.GetProperty("from").GetInt64(),
                    HarnessJson.ReadMessages(p.GetProperty("messages"))));
                w.WriteEndObject();
                break;
            case ProtocolOps.CreateAssignment:
                store.CreateAssignment(HarnessJson.ReadAssignment(p.GetProperty("assignment")));
                w.WriteStartObject(); w.WriteEndObject();
                break;
            case ProtocolOps.Settle:
                store.SettleAssignment(new SettleAssignmentRequest(
                    p.GetProperty("assignmentId").GetString()!, p.GetProperty("resultText").GetString()!));
                w.WriteStartObject(); w.WriteEndObject();
                break;
            case ProtocolOps.GetAssignment:
                HarnessJson.WriteAssignment(w, store.GetAssignment(p.GetProperty("assignmentId").GetString()!));
                break;
            case ProtocolOps.DeleteSession:
                store.DeleteSession(
                    p.GetProperty("sessionId").GetString()!,
                    Enum.Parse<DeletePolicy>(p.GetProperty("policy").GetString()!));
                w.WriteStartObject(); w.WriteEndObject();
                break;
            case ProtocolOps.Statistics:
                if (statistics == null) throw new InvalidOperationException("Store does not implement IStoreStatistics");
                var stats = statistics.Sample();
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

    private void EmitResponse(long id, string? error, JsonElement? result)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("i", id);
            if (error != null) writer.WriteString("e", error); else writer.WriteNull("e");
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