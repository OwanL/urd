using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Urd.Core.Sessions;

namespace Urd.Core.Execution;

// Native updates supply previews only. The validated call path alone can reuse an entry ID.
internal sealed class ToolDraftBuffer(Guid executionId, Guid requestId, Lock gate,
    Action<ToolDraftUpdate, Func<ToolDraftUpdate>?> publish) : IDisposable {
  internal const string PropertyName = "urd.tool_draft_buffer";
  internal const int CallLimit = 16;
  // Oversized provider metadata is not retained by this display-only buffer.
  internal const int IdentityLimit = 1024;
  private static readonly long PublishIntervalTicks = 75 * Stopwatch.Frequency / 1000;
  private readonly Dictionary<int, Call> calls = [];
  private Timer? timer;
  private long flushAt = long.MaxValue;
  private bool retired;

  internal void Append(int index, string? callId, string? name, ReadOnlySpan<byte> delta) =>
      Update(index, callId, name, delta, complete: false);

  internal void AppendChat(int index, string? callId, string? name, ReadOnlySpan<byte> delta) =>
      Update(index, callId, name, delta, complete: false, resume: !delta.IsEmpty);

  internal void Complete(int index, string? callId, string? name, ReadOnlySpan<byte> fullArguments = default) =>
      Update(index, callId, name, fullArguments, complete: true);

  internal void CompleteItem(int index, string? callId, string? name, ReadOnlySpan<byte> fullArguments) =>
      Update(index, callId, name, fullArguments, complete: true, authoritative: true);

  private void Update(int index, string? callId, string? name, ReadOnlySpan<byte> arguments, bool complete,
      bool resume = false, bool authoritative = false) {
    lock (gate) {
      if (retired || index < 0)
        return;
      if (!calls.TryGetValue(index, out Call? call)) {
        if (calls.Count == CallLimit)
          return;
        call = new Call();
        calls.Add(index, call);
        // Native output indexes can include non-call items or arrive out of order.
        // Update already visible rows when discovering a preceding call.
        long now = Stopwatch.GetTimestamp();
        foreach (var (otherIndex, other) in calls) {
          if (otherIndex > index && other.ReadLatest is not null && !other.Promoted)
            Publish(otherIndex, other, now);
        }
      }
      if (call.Promoted)
        return;
      if (IsDisplayIdentity(callId))
        call.Id ??= callId;
      if (IsDisplayIdentity(name))
        call.Name ??= name;
      bool wasDrafted = call.Drafted;
      if (authoritative || ((!wasDrafted || resume) && !arguments.IsEmpty)) {
        if (complete)
          call.Arguments.Reset();
        call.Arguments.Append(arguments);
      }
      if (!wasDrafted || resume || authoritative)
        call.Drafted = complete;
      if (call.Id is null || call.Name is null)
        return;
      if (call.ReadLatest is null) {
        if (calls.Values.Any(other => other != call && other.Id == call.Id && other.ReadLatest is not null))
          return;
        call.ReadLatest = () => WithPreview(Snapshot(index, call, LiveState(call)), call);
        Publish(index, call, Stopwatch.GetTimestamp());
      } else if ((!wasDrafted || resume) && (complete || !arguments.IsEmpty)
          || authoritative || complete && call.Dirty) {
        long now = Stopwatch.GetTimestamp();
        long due = call.LastPublished + PublishIntervalTicks;
        if (complete || now >= due) {
          Publish(index, call, now);
        } else if (!call.Dirty) {
          call.Dirty = true;
          Schedule(due, now);
        }
      }
    }
  }

  // The native stream has closed and only these calls can reach reservation.
  internal void Reconcile(IReadOnlyList<FunctionCallContent> validatedCalls) {
    lock (gate) {
      foreach (var (index, call) in calls) {
        if (call.ReadLatest is null)
          continue;
        call.Dirty = false;
        var validated = validatedCalls.FirstOrDefault(candidate => candidate.CallId == call.Id);
        if (validated is null) {
          call.ReadLatest = null;
          publish(Snapshot(index, call, "discarded"), null);
        } else {
          call.Name = validated.Name;
          call.Arguments.Reset();
          call.Arguments.Append(JsonSerializer.SerializeToUtf8Bytes(validated.Arguments));
        }
      }
    }
  }

  internal Guid? FindEntryId(string callId) {
    lock (gate)
      return calls.Values.FirstOrDefault(call => call.ReadLatest is not null && call.Id == callId)?.EntryId;
  }

  internal void Promote(Guid entryId) {
    lock (gate) {
      foreach (var (order, call) in calls) {
        if (call.EntryId != entryId || call.ReadLatest is null || call.Promoted)
          continue;
        call.Promoted = true;
        ToolDraftUpdate promoted = Snapshot(order, call, "promoted");
        publish(promoted, () => WithPreview(promoted, call));
        return;
      }
    }
  }

  internal void Retire() {
    lock (gate) {
      if (retired)
        return;
      retired = true;
      timer?.Dispose();
      foreach (var (order, call) in calls) {
        if (call.ReadLatest is not null && !call.Promoted)
          publish(Snapshot(order, call, "discarded"), null);
      }
      calls.Clear();
    }
  }

  public void Dispose() => Retire();

  private void Schedule(long due, long now) {
    if (due >= flushAt)
      return;
    flushAt = due;
    timer ??= new Timer(static state => ((ToolDraftBuffer)state!).Flush(), this, Timeout.Infinite, Timeout.Infinite);
    timer.Change((int)Math.Ceiling(Stopwatch.GetElapsedTime(now, due).TotalMilliseconds), Timeout.Infinite);
  }

  private void Flush() {
    lock (gate) {
      if (retired)
        return;
      flushAt = long.MaxValue;
      long now = Stopwatch.GetTimestamp(), next = long.MaxValue;
      foreach (var (index, call) in calls) {
        if (!call.Dirty)
          continue;
        long due = call.LastPublished + PublishIntervalTicks;
        if (due <= now)
          Publish(index, call, now);
        else
          next = Math.Min(next, due);
      }
      Schedule(next, now);
    }
  }

  private void Publish(int index, Call call, long now) {
    call.Dirty = false;
    call.LastPublished = now;
    publish(Snapshot(index, call, LiveState(call)), call.ReadLatest);
  }

  private static string LiveState(Call call) => call.Drafted ? "drafted" : "drafting";

  private ToolDraftUpdate Snapshot(int index, Call call, string state) {
    int order = calls.Keys.Count(other => other < index);
    return new(executionId, requestId, call.EntryId, call.Id!, call.Name!, order, state, call.Arguments.Count, "", "", call.Arguments.Field);
  }

  private static ToolDraftUpdate WithPreview(ToolDraftUpdate draft, Call call) {
    var (head, tail) = call.Arguments.Read();
    return draft with { Head = head, Tail = tail, Field = call.Arguments.Field };
  }

  private static bool IsDisplayIdentity(string? value) =>
      value is { Length: <= IdentityLimit } && !string.IsNullOrWhiteSpace(value);

  private sealed class Call {
    internal readonly Guid EntryId = Guid.NewGuid();
    internal readonly ToolDraftPreview Arguments = new();
    internal Func<ToolDraftUpdate>? ReadLatest;
    internal string? Id;
    internal string? Name;
    internal bool Drafted;
    internal bool Promoted;
    internal bool Dirty;
    internal long LastPublished;
  }
}
