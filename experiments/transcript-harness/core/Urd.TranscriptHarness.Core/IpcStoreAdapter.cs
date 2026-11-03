using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Urd.TranscriptHarness.Contracts;

namespace Urd.TranscriptHarness.Core;

/// <summary>
/// Adapter-side client: runs ITranscriptStore against a store host child
/// process using the JSON-line protocol. Strict synchronous request/response;
/// kill supervision sends requests without awaiting them.
/// </summary>
public sealed class IpcStoreAdapter : ITranscriptStore
{
    private readonly Process _host;
    private long _nextId = 1;

    private IpcStoreAdapter(Process host) => _host = host;

    public ITranscriptStore Store => this;

    /// <summary>Spawns and opens a store host; returns once the "ready" event is seen.</summary>
    public static IpcStoreAdapter Spawn(StoreHostLaunch launch)
    {
        var psi = new ProcessStartInfo
        {
            FileName = launch.HostExecutable,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(launch.HostExecutable)!,
        };
        if (!string.IsNullOrEmpty(launch.StoreAssembly))
        {
            psi.ArgumentList.Add("--store-assembly");
            psi.ArgumentList.Add(launch.StoreAssembly);
        }
        psi.ArgumentList.Add("--store-type");
        psi.ArgumentList.Add(launch.StoreType);
        var host = Process.Start(psi) ?? throw new HarnessError("Failed to spawn store host");
        var adapter = new IpcStoreAdapter(host);
        try
        {
            adapter.AwaitReady();
        }
        catch
        {
            try { adapter.KillNow(); } catch { /* best effort */ }
            host.Dispose();
            throw;
        }
        adapter.Open(launch.StoreRoot, launch.AttachmentRoot);
        return adapter;
    }

    public StoreDescriptor Describe()
    {
        var id = Send(ProtocolOps.Describe, static _ => { });
        var element = AwaitResponse(id);
        return new StoreDescriptor(
            element.GetProperty("storeName").GetString()!,
            Enum.Parse<AckTier>(element.GetProperty("ackTier").GetString()!),
            element.GetProperty("settingsNote").GetString()!,
            element.GetProperty("notes").GetString()!);
    }

    public sealed record StoreHostLaunch(string HostExecutable, string? StoreAssembly, string StoreType, string StoreRoot, string AttachmentRoot);

    public Process Process => _host;

    public void KillNow() => _host.Kill(entireProcessTree: true);

    // ---- ITranscriptStore over IPC ----

    public void Open(string storeRoot, string attachmentRoot) =>
        Call(ProtocolOps.Open, static (w, p) =>
        {
            w.WriteString("root", p.Item1);
            w.WriteString("attachmentRoot", p.Item2);
        }, (storeRoot, attachmentRoot), static _ => (object?)null);

    public SessionMeta[] ListSessionsMeta() =>
        Call(ProtocolOps.ListSessions, HarnessJson.ReadSessionMetas);

    public MessageRecord[] OpenRecent(string sessionId, int count) =>
        Call(ProtocolOps.OpenRecent, w =>
        {
            w.WriteString("sessionId", sessionId);
            w.WriteNumber("count", count);
        }, HarnessJson.ReadMessages);

    public MessageRecord[] PageOlder(string sessionId, long beforeOrdinal, int count) =>
        Call(ProtocolOps.PageOlder, w =>
        {
            w.WriteString("sessionId", sessionId);
            w.WriteNumber("before", beforeOrdinal);
            w.WriteNumber("count", count);
        }, HarnessJson.ReadMessages);

    public MessageRecord[] ReadRange(string sessionId, long fromOrdinal, long toOrdinalExclusive) =>
        Call(ProtocolOps.ReadRange, w =>
        {
            w.WriteString("sessionId", sessionId);
            w.WriteNumber("from", fromOrdinal);
            w.WriteNumber("to", toOrdinalExclusive);
        }, HarnessJson.ReadMessages);

    public WindowPreview OpenRecentPreview(string sessionId, int count, int previewChars) =>
        Call(ProtocolOps.WindowPreview, w =>
        {
            w.WriteString("sessionId", sessionId);
            w.WriteNumber("count", count);
            w.WriteNumber("previewChars", previewChars);
        }, HarnessJson.ReadWindowPreview);

    public string FetchToolResult(string sessionId, long ordinal, int partIndex) =>
        Call(ProtocolOps.FetchToolResult, w =>
        {
            w.WriteString("sessionId", sessionId);
            w.WriteNumber("ordinal", ordinal);
            w.WriteNumber("partIndex", partIndex);
        }, e => e.GetProperty("content").GetString()!);

    public long AppendMessage(string sessionId, MessageRecord message) =>
        Call(ProtocolOps.Append, w =>
        {
            w.WriteString("sessionId", sessionId);
            w.WritePropertyName("message");
            HarnessJson.WriteMessage(w, message);
        }, e => e.GetProperty("ordinal").GetInt64());

    public void StreamDraft(string sessionId, string streamId, string chunk) =>
        Call(ProtocolOps.StreamDraft, w =>
        {
            w.WriteString("sessionId", sessionId);
            w.WriteString("streamId", streamId);
            w.WriteString("chunk", chunk);
        });

    public long FinalizeStream(string sessionId, string streamId, MessageRecord finalMessage) =>
        Call(ProtocolOps.Finalize, w =>
        {
            w.WriteString("sessionId", sessionId);
            w.WriteString("streamId", streamId);
            w.WritePropertyName("message");
            HarnessJson.WriteMessage(w, finalMessage);
        }, e => e.GetProperty("ordinal").GetInt64());

    public long ReplaceSuffix(string sessionId, long fromOrdinal, IReadOnlyList<MessageRecord> newMessages) =>
        Call(ProtocolOps.ReplaceSuffix, w =>
        {
            w.WriteString("sessionId", sessionId);
            w.WriteNumber("from", fromOrdinal);
            w.WriteStartArray("messages");
            foreach (var message in newMessages) HarnessJson.WriteMessage(w, message);
            w.WriteEndArray();
        }, e => e.GetProperty("count").GetInt64());

    public void CreateAssignment(AssignmentRecord assignment) =>
        Call(ProtocolOps.CreateAssignment, w =>
        {
            w.WritePropertyName("assignment");
            HarnessJson.WriteAssignment(w, assignment);
        });

    public void SettleAssignment(SettleAssignmentRequest request) =>
        Call(ProtocolOps.Settle, w =>
        {
            w.WriteString("assignmentId", request.AssignmentId);
            w.WriteString("resultText", request.ResultText);
        });

    public AssignmentRecord GetAssignment(string assignmentId) =>
        Call(ProtocolOps.GetAssignment, w => w.WriteString("assignmentId", assignmentId),
            HarnessJson.ReadAssignment);

    public void DeleteSession(string sessionId, DeletePolicy policy) =>
        Call(ProtocolOps.DeleteSession, w =>
        {
            w.WriteString("sessionId", sessionId);
            w.WriteString("policy", policy.ToString());
        });

    // ---- protocol plumbing ----

    public StoreStatistics? QueryStatistics()
    {
        try
        {
            var id = Send(ProtocolOps.Statistics, static _ => { });
            return TryRead(id, out var response)
                ? new StoreStatistics(response.GetProperty("managedAllocatedBytes").GetInt64())
                : null;
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("Store does not implement IStoreStatistics"))
        {
            return null; // store has no IStoreStatistics; other failures propagate
        }
    }

    /// <summary>Clean shutdown; resolves to a zero non-error exit after "close".</summary>
    public void Close()
    {
        var id = Send(ProtocolOps.Close, static _ => { });
        TryRead(id, out _);
        FlushInput();
        if (!_host.WaitForExit(10_000)) _host.Kill(entireProcessTree: true);
        _host.WaitForExit();
    }

    public void Dispose()
    {
        try
        {
            if (!_host.HasExited)
            {
                FlushInput();
                if (!_host.WaitForExit(5_000)) _host.Kill(entireProcessTree: true);
            }
        }
        finally
        {
            _host.Dispose();
        }
    }

    public void FlushInput() => _host.StandardInput.Flush();
    public void WriteLine(string line) => _host.StandardInput.WriteLine(line);
    public void SendRaw(string line) => WriteLine(line);

    private void AwaitReady()
    {
        while (true)
        {
            var line = _host.StandardOutput.ReadLine()
                ?? throw new HarnessError($"Store host exited before ready. stderr: '{_host.StandardError.ReadToEnd()}'");
            var element = JsonDocument.Parse(line).RootElement;
            if (element.TryGetProperty("ev", out var ev))
            {
                if (ev.GetString() == Protocol.Ready) return;
                if (ev.GetString() == Protocol.Fatal)
                {
                    throw new HarnessError($"Store host fatal: {element.GetProperty("p")}");
                }
            }
            // Anything else before ready is a protocol violation.
            throw new HarnessError($"Unexpected pre-ready response: {line}");
        }
    }

    private void Call(string op, Action<Utf8JsonWriter> writeParams) =>
        Call(op, (w, _) => writeParams(w), (object?)null, _ => (object?)null);

    private T Call<T>(string op, Action<Utf8JsonWriter> writeParams, Func<JsonElement, T> readResult) =>
        Call(op, (w, _) => writeParams(w), (object?)null, readResult);

    private T Call<T, TArg>(string op, Action<Utf8JsonWriter, TArg> writeParams, TArg arg, Func<JsonElement, T> readResult)
    {
        var id = Send(op, w => writeParams(w, arg));
        return readResult(AwaitResponse(id));
    }

    private T Call<T>(string op, Func<JsonElement, T> readResult)
    {
        var id = Send(op, static _ => { });
        return readResult(AwaitResponse(id));
    }

    private long Send(string op, Action<Utf8JsonWriter> writeParams)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("i", _nextId);
            writer.WriteString("op", op);
            writer.WriteStartObject("p");
            writeParams(writer);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        _nextId++;
        WriteLine(Encoding.UTF8.GetString(buffer.ToArray()));
        return _nextId - 1;
    }

    private JsonElement AwaitResponse(long id)
    {
        if (!TryRead(id, out var response))
        {
            throw new HarnessError($"No response for request {id} (host may have died)");
        }
        return response;
    }

    private bool TryRead(long id, out JsonElement result)
    {
        while (true)
        {
            var line = _host.StandardOutput.ReadLine();
            if (line is null)
            {
                result = default;
                return false;
            }
            var element = JsonDocument.Parse(line).RootElement;
            if (element.TryGetProperty("ev", out var ev))
            {
                if (ev.GetString() == Protocol.Fatal)
                {
                    throw new HarnessError($"Store host fatal: {element.GetProperty("p")}");
                }
                continue;
            }
            if (element.GetProperty("i").GetInt64() != id) continue;
            var error = element.GetProperty("e");
            if (error.ValueKind == JsonValueKind.String)
            {
                throw new InvalidOperationException($"op failed on store host: {error.GetString()}");
            }
            var r = element.GetProperty("r");
            result = r.ValueKind == JsonValueKind.Null ? JsonSerializer.SerializeToElement(new { }) : r.Clone();
            return true;
        }
    }
}