using System.Text;
using System.Text.Json;
using Urd.TranscriptHarness.Contracts;

namespace Urd.TranscriptHarness.Core;

/// <summary>
/// Single manual JSON codec for message parts/records, used identically for the
/// audit trace file, the IPC protocol and oracle comparisons. Kept explicit
/// (not reflection-based) so adapters see a fully frozen wire representation.
/// </summary>
public static class HarnessJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        IncludeFields = false,
    };

    public static void WritePart(Utf8JsonWriter w, MessagePart part)
    {
        w.WriteStartObject();
        switch (part)
        {
            case TextPart t:
                w.WriteString("kind", "text");
                w.WriteString("content", t.Content);
                break;
            case ToolCallPart c:
                w.WriteString("kind", "toolCall");
                w.WriteString("toolCallId", c.ToolCallId);
                w.WriteString("name", c.Name);
                w.WriteString("args", c.ArgsJson);
                break;
            case ToolResultPart r:
                w.WriteString("kind", "toolResult");
                w.WriteString("toolCallId", r.ToolCallId);
                w.WriteString("content", r.Content);
                break;
            case ImagePart i:
                w.WriteString("kind", "image");
                w.WriteString("imageId", i.ImageId);
                w.WriteString("relativePath", i.RelativePath);
                break;
            default:
                throw new HarnessError($"Unknown part type {part.GetType().Name}");
        }
        w.WriteEndObject();
    }

    public static MessagePart ReadPart(JsonElement e)
    {
        var kind = e.GetProperty("kind").GetString();
        return kind switch
        {
            "text" => new TextPart(e.GetProperty("content").GetString()!),
            "toolCall" => new ToolCallPart(
                e.GetProperty("toolCallId").GetString()!,
                e.GetProperty("name").GetString()!,
                e.GetProperty("args").GetString()!),
            "toolResult" => new ToolResultPart(
                e.GetProperty("toolCallId").GetString()!,
                e.GetProperty("content").GetString()!),
            "image" => new ImagePart(
                e.GetProperty("imageId").GetString()!,
                e.GetProperty("relativePath").GetString()!),
            _ => throw new HarnessError($"Unknown part kind '{kind}'"),
        };
    }

    public static void WriteMessage(Utf8JsonWriter w, MessageRecord m)
    {
        w.WriteStartObject();
        w.WriteString("role", m.Role);
        w.WriteStartArray("parts");
        foreach (var p in m.Parts) WritePart(w, p);
        w.WriteEndArray();
        w.WriteEndObject();
    }

    public static MessageRecord ReadMessage(JsonElement e)
    {
        var parts = new List<MessagePart>();
        foreach (var p in e.GetProperty("parts").EnumerateArray()) parts.Add(ReadPart(p));
        return new MessageRecord(e.GetProperty("role").GetString()!, parts);
    }

    // Session meta / content arrays, shared by protocol responses.

    public static void WriteMessages(Utf8JsonWriter w, IReadOnlyList<MessageRecord> messages)
    {
        w.WriteStartArray();
        foreach (var m in messages) WriteMessage(w, m);
        w.WriteEndArray();
    }

    public static MessageRecord[] ReadMessages(JsonElement e)
    {
        var list = new List<MessageRecord>();
        foreach (var m in e.EnumerateArray()) list.Add(ReadMessage(m));
        return [.. list];
    }

    public static void WriteSessionMetas(Utf8JsonWriter w, IReadOnlyList<SessionMeta> metas)
    {
        w.WriteStartArray();
        foreach (var meta in metas)
        {
            w.WriteStartObject();
            w.WriteString("sessionId", meta.SessionId);
            w.WriteNumber("messageCount", meta.MessageCount);
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    public static SessionMeta[] ReadSessionMetas(JsonElement e)
    {
        var list = new List<SessionMeta>();
        foreach (var s in e.EnumerateArray())
        {
            list.Add(new SessionMeta(
                s.GetProperty("sessionId").GetString()!,
                s.GetProperty("messageCount").GetInt64()));
        }
        return [.. list];
    }

    public static void WriteAssignment(Utf8JsonWriter w, AssignmentRecord a)
    {
        w.WriteStartObject();
        w.WriteString("assignmentId", a.AssignmentId);
        w.WriteString("targetSessionId", a.TargetSessionId);
        w.WriteString("title", a.Title);
        w.WriteString("status", a.Status.ToString());
        if (a.ResultText is { } t) w.WriteString("resultText", t);
        else w.WriteNull("resultText");
        w.WriteEndObject();
    }

    public static AssignmentRecord ReadAssignment(JsonElement e)
    {
        return new AssignmentRecord(
            e.GetProperty("assignmentId").GetString()!,
            e.GetProperty("targetSessionId").GetString()!,
            e.GetProperty("title").GetString()!,
            Enum.Parse<AssignmentStatus>(e.GetProperty("status").GetString()!),
            e.GetProperty("resultText").ValueKind == JsonValueKind.String
                ? e.GetProperty("resultText").GetString()! : null);
    }

    /// <summary>Stable canonical form used for oracle comparisons and trace hashing.</summary>
    public static byte[] CanonicalBytes(IReadOnlyList<MessageRecord> messages)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            WriteMessages(w, messages);
        }
        return ms.ToArray();
    }

    public static void WriteWindowPreview(Utf8JsonWriter w, WindowPreview preview)
    {
        w.WriteStartObject();
        w.WriteString("sessionId", preview.SessionId);
        w.WriteNumber("from", preview.FromOrdinal);
        w.WriteStartArray("items");
        foreach (var item in preview.Items)
        {
            w.WriteStartObject();
            w.WriteNumber("ordinal", item.Ordinal);
            w.WriteString("role", item.Role);
            w.WriteStartArray("parts");
            foreach (var p in item.Parts)
            {
                w.WriteStartObject();
                w.WriteString("kind", p.Kind);
                w.WriteNumber("chars", p.ContentChars);
                if (p.ToolCallId is { } id) w.WriteString("toolCallId", id); else w.WriteNull("toolCallId");
                w.WriteString("preview", p.Preview);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();
    }

    public static WindowPreview ReadWindowPreview(JsonElement e)
    {
        var items = new List<MessagePreview>();
        foreach (var item in e.GetProperty("items").EnumerateArray())
        {
            var parts = new List<PartPreview>();
            foreach (var p in item.GetProperty("parts").EnumerateArray())
            {
                parts.Add(new PartPreview(
                    p.GetProperty("kind").GetString()!,
                    p.GetProperty("chars").GetInt64(),
                    p.GetProperty("toolCallId").ValueKind == JsonValueKind.String
                        ? p.GetProperty("toolCallId").GetString() : null,
                    p.GetProperty("preview").GetString()!));
            }
            items.Add(new MessagePreview(
                item.GetProperty("ordinal").GetInt64(),
                item.GetProperty("role").GetString()!,
                parts));
        }
        return new WindowPreview(
            e.GetProperty("sessionId").GetString()!,
            e.GetProperty("from").GetInt64(),
            items);
    }
}

/// <summary>Harness-level correctness failure (distinct from store exceptions).</summary>
public sealed class HarnessError(string message) : Exception(message);

public sealed class HarnessVerificationError(string message) : Exception(message);