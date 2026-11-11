using Urd.Core.Execution;

namespace Urd.Core.Tools;

// Add owns completion (including result save) and cancellation before invocation starts.
internal sealed class PendingToolCalls {
  private readonly object gate = new();
  private readonly Dictionary<(Guid RequestId, string CallId), Entry> calls = [];
  private readonly Dictionary<Guid, Execution> executions = [];

  public void Add(Guid executionId, Guid requestId, LlmToolCall call,
      Task completion, CancellationTokenSource cancellation, Func<Exception, Task>? onFailure = null) {
    ArgumentNullException.ThrowIfNull(call);
    ArgumentNullException.ThrowIfNull(completion);
    ArgumentNullException.ThrowIfNull(cancellation);
    Entry entry;
    Execution execution;
    lock (gate) {
      if (calls.ContainsKey((requestId, call.CallId)))
        throw new InvalidOperationException("The pending tool call already exists.");
      if (!executions.TryGetValue(executionId, out execution!)) {
        execution = new Execution();
        executions.Add(executionId, execution);
      }
      if (execution.Join is not null)
        throw new InvalidOperationException("Cannot add a tool call after execution join begins.");
      entry = new Entry(executionId, new PendingToolCall(requestId, call.CallId, call.Name, DateTimeOffset.UtcNow, false), cancellation, onFailure);
      calls.Add((requestId, call.CallId), entry);
      execution.Entries.Add(entry);
    }
    _ = ObserveCompletionAsync(entry, execution, completion);
  }

  public IReadOnlyList<PendingToolCall> List() {
    lock (gate)
      return calls.Values.Where(entry => !entry.Ended)
        .Select(entry => entry.Call with { CancellationRequested = entry.CancellationFinished is not null }).ToArray();
  }

  public int RequestCancelAll(Guid callerRequestId, string callerCallId) => RequestCancel(entry =>
    entry.Call.RequestId != callerRequestId || entry.Call.CallId != callerCallId);

  public void RequestCancelExecution(Guid executionId) => RequestCancel(entry => entry.ExecutionId == executionId);

  public Task JoinExecutionAsync(Guid executionId) {
    lock (gate) {
      if (!executions.TryGetValue(executionId, out Execution? execution))
        return Task.CompletedTask;
      return execution.Join ??= JoinCoreAsync(executionId, execution);
    }
  }

  private int RequestCancel(Func<Entry, bool> includes) {
    Entry[] targets;
    lock (gate) {
      targets = calls.Values.Where(entry => !entry.Ended && entry.CancellationFinished is null && includes(entry)).ToArray();
      // Reserve callback ownership before releasing the lock: completion cannot dispose these sources.
      foreach (Entry entry in targets)
        entry.CancellationFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    foreach (Entry entry in targets)
      _ = CancelAsync(entry);
    return targets.Length;
  }

  private async Task CancelAsync(Entry entry) {
    Exception? failure = null;
    try {
      await entry.Cancellation.CancelAsync().ConfigureAwait(false);
    } catch (Exception error) {
      failure = error;
      NotifyFailure(entry, error);
    }
    entry.CancellationFinished!.SetResult(failure);
  }

  private void NotifyFailure(Entry entry, Exception failure) {
    if (entry.OnFailure is null)
      return;
    TaskCompletionSource<Exception?> notification;
    lock (gate) {
      if (entry.FailureNotification is not null)
        return; // Fence once for the first failure; Join still retains every failure.
      notification = new(TaskCreationOptions.RunContinuationsAsynchronously);
      entry.FailureNotification = notification;
    }
    _ = NotifyFailureAsync(entry.OnFailure, failure.GetBaseException(), notification);
  }

  private static async Task NotifyFailureAsync(Func<Exception, Task> onFailure, Exception failure,
      TaskCompletionSource<Exception?> notification) {
    Exception? notificationFailure = null;
    try {
      await onFailure(failure).ConfigureAwait(false);
    } catch (Exception error) {
      notificationFailure = error;
    }
    notification.SetResult(notificationFailure);
  }

  private async Task ObserveCompletionAsync(Entry entry, Execution execution, Task completion) {
    var failures = new List<Exception>();
    try {
      await completion.ConfigureAwait(false);
    } catch (Exception failure) {
      failures.Add(completion.Exception ?? failure);
      NotifyFailure(entry, failure);
    }
    Task<Exception?>? cancellation;
    lock (gate) {
      entry.Ended = true;
      cancellation = entry.CancellationFinished?.Task;
    }
    if (cancellation is not null && await cancellation.ConfigureAwait(false) is Exception callbackFailure)
      failures.Add(callbackFailure);
    try {
      entry.Cancellation.Dispose();
    } catch (Exception failure) {
      failures.Add(failure);
      NotifyFailure(entry, failure);
    }
    Task<Exception?>? notification;
    lock (gate)
      notification = entry.FailureNotification?.Task;
    if (notification is not null && await notification.ConfigureAwait(false) is Exception notificationFailure)
      failures.Add(notificationFailure);
    lock (gate) {
      calls.Remove((entry.Call.RequestId, entry.Call.CallId));
      execution.Entries.Remove(entry);
      execution.Failures.AddRange(failures);
    }
    entry.Settled.SetResult();
  }

  private async Task JoinCoreAsync(Guid executionId, Execution execution) {
    await Task.Yield(); // Never join or run cancellation callbacks under the registry lock.
    Task[] settled;
    lock (gate)
      settled = execution.Entries.Select(entry => entry.Settled.Task).ToArray();
    await Task.WhenAll(settled).ConfigureAwait(false);
    Exception[] failures;
    lock (gate) {
      failures = execution.Failures.ToArray();
      execution.Failures.Clear();
      if (failures.Length == 0)
        executions.Remove(executionId);
    }
    // Faulted join tasks retain evidence for repeated joins, not calls, sources or tool results.
    if (failures.Length != 0)
      throw new AggregateException("Pending tool call cleanup or result save failed.", failures);
  }

  private sealed record Entry(Guid ExecutionId, PendingToolCall Call, CancellationTokenSource Cancellation,
      Func<Exception, Task>? OnFailure) {
    public TaskCompletionSource Settled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource<Exception?>? CancellationFinished { get; set; }
    public TaskCompletionSource<Exception?>? FailureNotification { get; set; }
    public bool Ended { get; set; }
  }

  private sealed record Execution {
    public HashSet<Entry> Entries { get; } = new(ReferenceEqualityComparer.Instance);
    public List<Exception> Failures { get; } = [];
    public Task? Join { get; set; }
  }
}

internal sealed record PendingToolCall(
  Guid RequestId, string CallId, string Name, DateTimeOffset StartedAt, bool CancellationRequested);
