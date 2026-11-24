using Urd.Core.Context;
using Urd.Core.Execution;
using Urd.Core.Preferences;
using Urd.Core.Providers;
using Urd.Core.Storage;
using Urd.Core.Tools;

namespace Urd.Core.Sessions;

// Runs bounded subagents and settles their resources.
internal sealed class SubagentRuns(
    UrdStore store, PreferenceManager preferences, ModelSelector? modelSelector, AgentQuestions questions,
    SessionRegistry registry, Func<SessionRecord, int, SubagentSession> createChild, Func<Guid, Task> closeBrowserOwner) {
  // Runs one bounded child to its final report. The child is registered under the shutdown fence
  // before any of its work runs, cancellation stops and joins it, and its processes and registry
  // entry are released only after the run finishes. Cross-session waits hold no manager locks.
  internal async Task<SubagentRunResult> RunSubagentAsync(HarnessToolContext caller, SubagentRunRequest request,
      CancellationToken cancellation) {
    ArgumentNullException.ThrowIfNull(caller);
    ArgumentNullException.ThrowIfNull(request);
    SubagentAssignment assignment = await PrepareAssignmentAsync(caller, request, cancellation);

    SubagentSession? child = null;
    try {
      child = await CreateSubagentAsync(assignment, new(caller.SessionId, caller.ExecutionId, caller.Background), cancellation);
      return await RunChildAsync(child, assignment, cancellation);
    } finally {
      if (child is not null) {
        await CleanupChildAsync(child);
      }
    }
  }

  private async Task CleanupChildAsync(SubagentSession child) {
    try {
      await Task.WhenAll(child.KillProcessesAsync(), closeBrowserOwner(child.Id));
    } catch (Exception failure) {
      throw new ToolCleanupException("Subagent resource cleanup failed.", failure);
    } finally {
      questions.CloseRecipient(child.Id);
      await registry.UntrackChildAsync(child.Id);
    }
  }

  // Reads the parent identity, inherits its working directory, resolves the bucket chat and
  // captures the chosen parent context. A misdirected caller context fails here, not in the child.
  private async Task<SubagentAssignment> PrepareAssignmentAsync(HarnessToolContext caller,
      SubagentRunRequest request, CancellationToken cancellation) {
    ArgumentException.ThrowIfNullOrWhiteSpace(request.Task);
    SessionRecord parent = store.Sessions.Read(caller.SessionId)
        ?? throw new InvalidOperationException($"The calling session '{caller.SessionId}' does not exist.");
    UserPreferences captured = preferences.Read();
    int childDepth = GetChildDepth(parent, captured.MaxSubagentDepth);
    if (modelSelector is null)
      throw new InvalidOperationException("Subagent buckets require a configured model selector.");
    LlmSelection selection = await modelSelector.ResolveSubagentAsync(request.Bucket, captured,
        store.Sessions.ReadSubagentProviders(parent.Id), cancellation);
    string? contextBlock = ReadParentContext(parent, caller, request, captured.CompactionTriggerTokenCount);
    return new(parent.Id, childDepth, caller.RequestId, caller.CallId, parent.WorkingDirectory, request.Task, contextBlock, selection);
  }

  private int GetChildDepth(SessionRecord parent, int maxDepth) {
    // The primary is at depth zero. Bound the walk even if the limit was lowered mid-run.
    SessionRecord ancestor = parent;
    for (int depth = 0; depth < maxDepth; depth++) {
      if (ancestor.Kind == SessionKind.Primary)
        return depth + 1;
      if (depth + 1 == maxDepth)
        break;
      ancestor = ancestor.ParentSessionId is Guid id && store.Sessions.Read(id) is { } record
          ? record : throw new InvalidOperationException("The calling session has no valid parent.");
    }
    throw NestingLimit(maxDepth);
  }

  private static InvalidOperationException NestingLimit(int maxDepth) =>
      new($"Subagent nesting limit reached ({maxDepth} levels below the primary). Finish the task without delegating.");

  private string? ReadParentContext(SessionRecord parent, HarnessToolContext caller, SubagentRunRequest request,
      int? parentCompactionTriggerTokens) {
    if (request.Context == SubagentContextMode.None)
      return null;

    // Only Recent budgets need the parent's own chat selection; stored facts are enough here.
    LlmSelection? parentSelection = request.Context == SubagentContextMode.Recent
        ? store.Snapshots.ReadRunSelection(parent.Id, caller.ExecutionId) : null;
    return store.Subagents.Read(new ExecutionIdentity(parent.Id, caller.ExecutionId, parent.HistoryRevision),
        caller.RequestId, caller.CallId, request.Context, parentSelection?.ContextWindow, parentCompactionTriggerTokens);
  }

  private Task<SubagentSession> CreateSubagentAsync(SubagentAssignment assignment, AgentWaitSource owner,
      CancellationToken cancellation) =>
    registry.RunOpenAsync(() => {
      // Serialize admission with user toggles. The model may have returned a tool call from an
      // older request, or the provider may have been disabled while routing was in progress.
      if (store.Sessions.ReadSubagentProviders(assignment.ParentSessionId) is { } allowed
          && !allowed.Contains(assignment.Selection.Connection.Id))
        throw new AuxiliaryModelUnavailableException("This session no longer allows the selected subagent provider.");
      // Serialize durable creation with the global depth setting as well.
      SessionRecord record = store.Preferences.WithCurrent(current => assignment.Depth > current.MaxSubagentDepth
          ? throw NestingLimit(current.MaxSubagentDepth)
          : store.Sessions.CreateSubagent(Guid.NewGuid(), assignment.ParentSessionId, assignment.OwningCallId,
              assignment.WorkingDirectory, assignment.OwningRequestId));
      // Install ownership before publishing the handle or starting the assignment.
      var child = createChild(record, assignment.Depth);
      questions.TrackDelegation(child.Id, owner);
      // Children are deliberately absent from SessionCreated and the primary tab list.
      registry.TrackChild(child);
      return child;
    }, cancellation);

  private async Task<SubagentRunResult> RunChildAsync(SubagentSession child, SubagentAssignment assignment,
      CancellationToken cancellation) {
    bool owned = true;
    try {
      Guid executionId;
      try {
        executionId = await child.StartAssignmentAsync(assignment.Task, assignment.ContextBlock, assignment.Selection);
      } catch (Exception) when (cancellation.IsCancellationRequested) {
        // Creation saw the cancellation after registration: stop the owned child before it runs.
        owned = false;
        await StopChildAsync(child);
        return Error(child.Id, CancelledReport);
      }

      try {
        SessionExecutionResult settled = await child.WaitForExecutionFinishAsync(executionId, CancellationToken.None)
            .WaitAsync(cancellation);
        SubagentRunResult result = ReadReport(child.Id, settled);
        owned = false;
        return result;
      } catch (OperationCanceledException) {
        // Cancellation stops the child and joins its settlement before the report is lost with it.
        owned = false;
        await StopChildAsync(child);
        return Error(child.Id, CancelledReport);
      }
    } finally {
      if (owned) {
        await StopChildAsync(child);
      }
    }
  }

  private static async Task StopChildAsync(SubagentSession child) {
    try {
      await child.StopExecutionAsync();
    } catch (Exception failure) {
      throw new ToolCleanupException("Subagent stop cleanup failed.", failure);
    }
  }

  private SubagentRunResult ReadReport(Guid childSessionId, SessionExecutionResult settled) {
    switch (settled.Outcome) {
      case ExecutionOutcome.Completed:
        string report = store.Executions.ReadFinalReport(childSessionId, settled.ExecutionId);
        return string.IsNullOrWhiteSpace(report)
            ? Error(childSessionId, "The subagent task produced no report.")
            : new(childSessionId, report, Error: null);
      case ExecutionOutcome.Cancelled:
        return Error(childSessionId, CancelledReport);
      default:
        return Error(childSessionId, settled.Error is { } failure
            ? $"The subagent task failed: {failure}" : "The subagent task failed.");
    }
  }

  private static SubagentRunResult Error(Guid childSessionId, string message) =>
      new(childSessionId, Report: null, Error: message);

  // Final result message for runs the parent cannot observe a report from.
  private const string CancelledReport = "The subagent task was cancelled.";

  // One prepared bounded child: ownership, inherited directory and its whole assignment.
  private sealed record SubagentAssignment(Guid ParentSessionId, int Depth, Guid OwningRequestId, string OwningCallId,
      string? WorkingDirectory, string Task, string? ContextBlock, LlmSelection Selection);
}
