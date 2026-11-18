using System.Runtime.ExceptionServices;
using Microsoft.Extensions.AI;
using Urd.Core.Context;
using Urd.Core.Compaction;
using Urd.Core.Sessions;
using Urd.Core.Providers;
using Urd.Core.Preferences;
using Urd.Core.Tools;
using Urd.Core.Usage;

namespace Urd.Core.Execution;

// Prepares each LLM request and settles its response; LlmResponseRunner owns stream cleanup.
// SessionExecution owns request cancellation; SessionToolExecution owns tool batches.
// SessionExecutionDriver chooses the next request and waits for cleanup before settling the execution.
internal sealed class SessionExecution : IDisposable {
  private readonly SessionBase session;
  private readonly SessionExecutionRecorder recorder;
  private readonly Func<ProviderConnection, IChatClient> createChatClient; // Returns borrowed clients.
  private readonly LlmSelection selection;
  private readonly StartingContextBuilder startingContextBuilder;
  private StartingContext? startingContext;
  private IChatClient? chatClient;
  private readonly SessionToolExecution toolExecution;
  private readonly PendingToolCalls pending;
  private readonly ConversationRunner auxiliary;
  private Task? titleWork;
  private int titleFinished;
  private Guid? automaticBoundary;
  private readonly CancellationTokenSource stop = new();
  private Task? stopCallbacks;
  private readonly ManualCompactionRequest? manualCompactionRequest;

  internal SessionExecution(SessionExecutionRecorder recorder, SessionBase session,
      Func<ProviderConnection, IChatClient> createChatClient, LlmSelection selection,
      StartingContextBuilder startingContextBuilder, IReadOnlyList<AgentTool>? tools = null,
      PendingToolCalls? pendingToolCalls = null, ManualCompactionRequest? compaction = null) {
    this.recorder = recorder;
    this.session = session;
    this.createChatClient = createChatClient;
    this.selection = selection;
    this.startingContextBuilder = startingContextBuilder;
    pending = pendingToolCalls ?? new PendingToolCalls();
    toolExecution = new SessionToolExecution(session, recorder, tools ?? [], pending, selection.SupportsImages, stop.Token);
    auxiliary = new(recorder, createChatClient, tools ?? [], session.WorkingDirectory);
    manualCompactionRequest = compaction;
    IsCompacting = compaction is not null;
  }

  internal ExecutionIdentity Identity => recorder.Identity;
  internal SessionExecutionRecorder Recorder => recorder;
  internal Guid ExecutionId => Identity.ExecutionId;
  internal bool IsStopping => stop.IsCancellationRequested;
  internal bool IsManualCompaction => manualCompactionRequest is not null;
  internal bool IsCompacting { get; set; }
  internal bool HasPendingTools => toolExecution.HasRunningBackgroundTools;
  internal bool HasPendingAuxiliary => titleWork is not null && Volatile.Read(ref titleFinished) == 0;
  internal Task JoinOwnedWorkAsync() => Task.WhenAll(toolExecution.JoinBackgroundToolsAsync(), titleWork ?? Task.CompletedTask);

  // Completes once every callback registered on the stop token has finished.
  internal Task StopCallbacks => stopCallbacks ?? Task.CompletedTask;

  // Called by the session while it holds its lock. Repeated calls share one task.
  internal Task StopAsync() {
    if (stopCallbacks is not null)
      return stopCallbacks;
    stopCallbacks = stop.CancelAsync();
    toolExecution.CancelBackgroundTools();
    return stopCallbacks;
  }

  // Called by the session only after every request and StopCallbacks have finished
  // and the execution's result has been saved.
  public void Dispose() => stop.Dispose();

  // Called sequentially outside the session lock. Foreground calls join here;
  // registered background calls remain owned until the session drains them.
  internal async Task<ExecutionOutcome> RunRequestAsync(LlmRequest request) {
    LlmRequestResult result = await SendLlmRequestAsync(request);
    // Retry only the rejected model exchange, never a tool or any observed partial output.
    bool canCompactAndRetry = result.Failure == ProviderFailure.ContextWindowExceeded
        && !result.OutputObserved && !IsStopping;
    if (canCompactAndRetry && await session.CanRetryOverflowAsync(ExecutionId, request.RequestId)) {
      try {
        LlmRequest? reducedRequest = await CompactHistoryAsync(request.RequestId, CompactionReason.Overflow);
        if (reducedRequest is not null)
          result = await SendLlmRequestAsync(reducedRequest, allowAutomaticCompaction: false);
      } catch (Exception failure) {
        result = CreateRequestResult(request.RequestId, failure);
      }
    }

    bool requestSaved = await recorder.CompleteRequestAsync(result);
    if (!requestSaved)
      return ExecutionOutcome.Cancelled;
    if (result.Outcome != ExecutionOutcome.Completed)
      return result.Outcome;

    return await toolExecution.RunBatchAsync(request.RequestId, result.ToolCalls ?? [], startingContext?.Tools ?? []);
  }

  internal async Task<ExecutionOutcome> RunCompactionAsync() {
    try {
      if (await CompactHistoryAsync(null, CompactionReason.Manual, manualCompactionRequest!.Focus) is null)
        await session.RecordCompactionErrorAsync(ExecutionId, "No older history can be compacted without dropping recent context.");
      return IsStopping ? ExecutionOutcome.Cancelled : ExecutionOutcome.Completed;
    } catch (OperationCanceledException) when (IsStopping) {
      return ExecutionOutcome.Cancelled;
    } catch (CompactionSaveException) {
      throw;
    } catch (Exception failure) {
      await session.RecordCompactionErrorAsync(ExecutionId, failure is ProviderOperationException provider
          ? provider.Failure.ToString() : "History compaction failed; the previous context is unchanged.");
      return ExecutionOutcome.Failed;
    }
  }

  // Summary work runs outside the session lock. The commit rechecks Stop, revision and prior boundary.
  private async Task<LlmRequest?> CompactHistoryAsync(Guid? requestId, CompactionReason reason, string? focus = null,
      ModelHistory? currentHistory = null) {
    HistoryCompactionPlan? plan = await CreateCompactionPlanAsync(requestId, reason, focus, currentHistory);
    if (plan is null)
      return null;
    await session.SetCompactingAsync(ExecutionId, true);
    try {
      GeneratedCompactionSummary result = await GenerateHistorySummaryAsync(plan);
      stop.Token.ThrowIfCancellationRequested();
      bool saved;
      try {
        saved = await session.SaveCompactionAsync(ExecutionId, plan, result);
      } catch (Exception failure) {
        throw new CompactionSaveException(failure);
      }
      if (!saved)
        throw new OperationCanceledException("The compaction boundary was rejected.");
      ModelHistory compactedHistory = await session.ReadModelHistoryAsync(ExecutionId, requestId);
      if (reason != CompactionReason.Manual)
        automaticBoundary = compactedHistory.PreviousCompaction?.Id;
      return new(requestId ?? Guid.Empty, compactedHistory.ReadMessages(), compactedHistory);
    } finally {
      await session.SetCompactingAsync(ExecutionId, IsManualCompaction);
    }
  }

  private async Task<HistoryCompactionPlan?> CreateCompactionPlanAsync(Guid? requestId, CompactionReason reason,
      string? focus, ModelHistory? currentHistory) {
    stop.Token.ThrowIfCancellationRequested();
    UserPreferences preferences = session.ReadPreferences();
    if (reason == CompactionReason.Threshold && selection.ContextWindow is null
        && preferences.CompactionTriggerTokenCount is null)
      return null;
    ModelHistory history = currentHistory ?? await session.ReadModelHistoryAsync(ExecutionId, requestId);
    StartingContext currentContext = startingContext ?? session.ReadStartingContext()
        ?? startingContextBuilder.Create(ContextInput(), stop.Token);
    HistoryCompactionPlan? plan = HistoryCompactor.CreatePlan(history, selection, currentContext, reason, focus,
        preferences.CompactionTriggerTokenCount);
    return plan is null ? null : plan with {
      BackgroundToolCalls = await session.ReadCompactionBackgroundCallsAsync(ExecutionId, plan.FirstRetainedMessage.Location)
    };
  }

  private async Task<GeneratedCompactionSummary> GenerateHistorySummaryAsync(HistoryCompactionPlan plan) {
    chatClient ??= createChatClient(selection.Connection);
    try {
      return await HistoryCompactor.GenerateSummaryAsync(chatClient, selection, plan, stop.Token,
          new UsageContext(session.Id, ExecutionId, "compaction"));
    } catch (OperationCanceledException) when (IsStopping) {
      throw;
    } catch (Exception failure) {
      throw new InvalidOperationException("History compaction failed; the previous context is unchanged.", failure);
    }
  }

  private StartingContextInput ContextInput() => new(session.Kind, selection.Family, session.WorkingDirectory, null);

  private async Task PrepareStartingContextAsync(LlmRequest request) {
    Guid boundary = request.Context?.PreviousCompaction?.Id ?? Guid.Empty;
    if (startingContext?.BoundaryId == boundary)
      return;
    startingContext = session.ReadStartingContext(boundary);
    if (startingContext is not null)
      return;

    UserPreferences captured = session.ReadPreferences();
    string[] newMessages = request.Context is { } history
        ? history.Messages.Where(message => message.Location.RequestId == request.RequestId && message.StartsUserTurn)
            .Select(message => message.Message.Text).ToArray()
        : request.Messages.Where(message => message.Role == ChatRole.User).Select(message => message.Text).ToArray();
    string task = boundary == Guid.Empty ? string.Join("\n\n", newMessages)
        : request.Context!.PreviousCompaction!.Summary;
    if (boundary != Guid.Empty && automaticBoundary != boundary && newMessages.Length > 0)
      task += "\n\n" + string.Join("\n\n", newMessages);

    if (boundary == Guid.Empty && newMessages.Length > 0 && titleWork is null) {
      bool claimed;
      try { claimed = await recorder.TryClaimTitleAsync(); }
      catch (Exception failure) { throw new AuxiliaryExecutionException("Saving the title attempt failed.", failure); }
      if (claimed)
        titleWork = GenerateTitleAsync(TitlePrompt(newMessages), captured);
    }

    var pruner = new StartingResourcePruner(auxiliary,
        (purpose, cancellation) => ResolvePrepassModelAsync(purpose, captured, cancellation),
        status => SavePruningStatusAsync(boundary, status), task,
        (status, candidates, resultIds) => SavePruningProgressAsync(boundary, status, candidates, resultIds));
    HashSet<string> protectedTools = boundary == Guid.Empty ? []
        : pending.List().Select(call => call.Name).ToHashSet(StringComparer.Ordinal);
    startingContext = await startingContextBuilder.PrepareAsync(ContextInput(), boundary, task,
        captured, protectedTools, pruner, stop.Token);
    stop.Token.ThrowIfCancellationRequested();
  }

  private async Task SavePruningStatusAsync(Guid boundary, StartingResourcePruningStatus status) {
    try {
      if (await recorder.RecordPruningStatusAsync(boundary, status))
        return;
      stop.Token.ThrowIfCancellationRequested();
      throw new AuxiliaryExecutionException("The pruning status no longer accepts this execution.");
    } catch (OperationCanceledException) when (IsStopping) {
      throw;
    } catch (Exception failure) {
      await recorder.ReportAuxiliaryFailureAsync(failure);
      throw new AuxiliaryExecutionException("Saving pruning progress failed.", failure);
    }
  }

  private async Task SavePruningProgressAsync(Guid boundary, StartingResourcePruningStatus status,
      IReadOnlyList<PrepassResource> candidates, IReadOnlyList<string>? resultIds) {
    try {
      if (await recorder.RecordPruningProgressAsync(boundary, status, candidates, resultIds))
        return;
      stop.Token.ThrowIfCancellationRequested();
      throw new AuxiliaryExecutionException("The pruning detail no longer accepts this execution.");
    } catch (OperationCanceledException) when (IsStopping) {
      throw;
    } catch (Exception failure) {
      await recorder.ReportAuxiliaryFailureAsync(failure);
      throw new AuxiliaryExecutionException("Saving pruning details failed.", failure);
    }
  }

  private async Task SaveTitleOutcomeAsync(string state, string? error) {
    try {
      if (await recorder.RecordTitleOutcomeAsync(state, error))
        return;
      throw new AuxiliaryExecutionException("The title outcome no longer accepts this execution.");
    } catch (AuxiliaryExecutionException) {
      throw;
    } catch (Exception failure) {
      await recorder.ReportAuxiliaryFailureAsync(failure);
      throw new AuxiliaryExecutionException("Saving the title outcome failed.", failure);
    }
  }

  // A failed or cancelled generation publishes the truncated prompt as the final title; a rejected
  // fallback publication keeps the plain failure recording.
  private async Task SaveFallbackTitleAsync(string prompt, string error) {
    string? fallback = TitleGenerator.CreateFallback(prompt);
    if (fallback is not null && await recorder.PublishFallbackTitleAsync(fallback, error))
      return;
    await SaveTitleOutcomeAsync("failed", error);
  }

  // A primary titles itself from its first prompt; an inherited context block is quoted parent
  // data, so a subagent titles from its task input alone.
  private string TitlePrompt(string[] prompts) =>
      session.Kind == SessionKind.Subagent ? prompts[^1] : prompts[0];

  // An unset or unresolvable purpose choice runs on the run's own chat selection, recorded so the
  // UI can flag it; provider call failures after resolution keep their existing fallback behavior.
  private async Task<AuxiliaryModelResolution> ResolvePrepassModelAsync(AuxiliaryModelPurpose purpose,
      UserPreferences captured, CancellationToken cancellation) {
    try {
      return new AuxiliaryModelResolution(await session.ResolveAuxiliaryModelAsync(purpose, captured, cancellation), false);
    } catch (AuxiliaryModelUnavailableException) {
      return new AuxiliaryModelResolution(selection, true);
    }
  }

  // Title work never blocks or changes the execution. A failed or cancelled generation publishes
  // the first prompt as the final title, so the session is addressable once the execution settles.
  private async Task GenerateTitleAsync(string prompt, UserPreferences captured) {
    Exception? fatal = null;
    try {
      AuxiliaryModelResolution model = await ResolvePrepassModelAsync(AuxiliaryModelPurpose.Title, captured, stop.Token);
      string title = await TitleGenerator.GenerateAsync(auxiliary, model, prompt, stop.Token);
      try {
        if (!await recorder.PublishTitleAsync(title)) {
          stop.Token.ThrowIfCancellationRequested();
          throw new AuxiliaryExecutionException("The generated title was rejected by its owner.");
        }
      } catch (OperationCanceledException) when (IsStopping) {
        throw;
      } catch (Exception failure) {
        throw new AuxiliaryExecutionException("Saving the generated title failed.", failure);
      }
    } catch (AuxiliaryExecutionException failure) {
      fatal = failure;
      throw;
    } catch (OperationCanceledException) when (IsStopping) {
      // A cancelled generation still settles the one-shot attempt with the prompt fallback.
      try {
        await SaveFallbackTitleAsync(prompt, "Title generation was cancelled.");
      } catch (AuxiliaryExecutionException failure) {
        fatal = failure;
        throw;
      }
    } catch (Exception failure) {
      try {
        await SaveFallbackTitleAsync(prompt, failure.Message);
      } catch (AuxiliaryExecutionException recordingFailure) {
        fatal = recordingFailure;
        throw;
      }
    } finally {
      Volatile.Write(ref titleFinished, 1);
      await session.ReportToolOutcomeAsync(ExecutionId, fatal);
    }
  }

  private async Task<LlmRequestResult> SendLlmRequestAsync(LlmRequest request, bool allowAutomaticCompaction = true) {
    IChatClient client;
    IReadOnlyList<ChatMessage> messages;
    ChatOptions options;
    try {
      stop.Token.ThrowIfCancellationRequested();
      if (allowAutomaticCompaction)
        request = await CompactHistoryAsync(request.RequestId, CompactionReason.Threshold, currentHistory: request.Context) ?? request;
      await PrepareStartingContextAsync(request);
      client = chatClient ??= createChatClient(selection.Connection);
      options = new() {
        ModelId = selection.ModelId,
        Reasoning = selection.ReadReasoningEffort() is { } effort
            ? new ReasoningOptions { Effort = effort } : null,
        Tools = [.. startingContext!.Tools],
        AdditionalProperties = new() {
          [UsageContext.PropertyName] = new UsageContext(session.Id, ExecutionId, "conversation")
        }
      };
      await StartRequestAsync(request.RequestId, startingContext);
      messages = LlmMessageProjection.AdaptForModel(
          [.. startingContext.Instructions, .. request.Messages], selection.SupportsImages);
    } catch (Exception failure) {
      return CreateRequestResult(request.RequestId, failure);
    }

    LlmResponseRunner.Result response = await LlmResponseRunner.RunAsync(client, messages, options,
        request.RequestId, SaveChunkAsync,
        (callId, name) => RecordToolCallAsync(request.RequestId, callId, name), stop.Token);
    LlmRequestResult result = CreateRequestResult(request.RequestId, response.Failure);
    return result with {
      ToolCalls = result.Outcome == ExecutionOutcome.Completed ? response.ToolCalls : null,
      ProviderResponse = result.Outcome == ExecutionOutcome.Completed ? response.ProviderResponse : null,
      Usage = response.Usage,
      OutputObserved = response.OutputObserved
    };
  }

  private LlmRequestResult CreateRequestResult(Guid requestId, Exception? failure) {
    // A failed save is a session failure, not a failed LLM request.
    if (failure is OutputSaveException or RequestStartSaveException or CompactionSaveException or AuxiliaryExecutionException)
      ExceptionDispatchInfo.Throw(failure);

    bool stopped = stop.IsCancellationRequested;
    ExecutionOutcome outcome = failure switch {
      null when stopped => ExecutionOutcome.Cancelled,
      null => ExecutionOutcome.Completed,
      OutputRejectedException => ExecutionOutcome.Cancelled,
      OperationCanceledException when stopped => ExecutionOutcome.Cancelled,
      _ => ExecutionOutcome.Failed
    };
    string? error = outcome == ExecutionOutcome.Failed ? failure!.Message : null;
    return new(requestId, outcome, error, Failure: (failure as ProviderOperationException)?.Failure);
  }

  private Task StartRequestAsync(Guid requestId, StartingContext startingContext) =>
    RequireSavedAsync(recorder.StartRequestAsync(requestId, startingContext), static failure => new RequestStartSaveException(failure));

  private Task SaveChunkAsync(LlmResponseChunk chunk) =>
    RequireSavedAsync(recorder.RecordOutputAsync(chunk), static failure => new OutputSaveException(failure));

  private Task RecordToolCallAsync(Guid requestId, string callId, string? name) =>
    RequireSavedAsync(recorder.RecordToolCallAsync(requestId, callId, name), static failure => new OutputSaveException(failure));

  private static async Task RequireSavedAsync(Task<bool> save, Func<Exception, Exception> wrapFailure) {
    bool saved;
    try {
      saved = await save;
    } catch (Exception failure) {
      throw wrapFailure(failure);
    }
    if (!saved)
      throw new OutputRejectedException();
  }

#pragma warning disable S3871 // Private driver signals, not public exception contracts.
  // The session no longer accepts output from this execution.
  private sealed class OutputRejectedException : Exception;

  private sealed class CompactionSaveException(Exception inner) : Exception("Saving history compaction failed.", inner);

  private sealed class OutputSaveException(Exception inner) : Exception("Saving response output failed.", inner);

  private sealed class RequestStartSaveException(Exception inner) : Exception("Saving the request attempt failed.", inner);
#pragma warning restore S3871
}
