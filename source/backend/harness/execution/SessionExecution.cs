using System.Runtime.ExceptionServices;
using Microsoft.Extensions.AI;
using Urd.Core.Context;
using Urd.Core.Compaction;
using Urd.Core.Harness.Mcp;
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
  private readonly IRemoteMcpTools? remoteMcpTools;
  private readonly int maxRemoteToolCount;
  private IReadOnlyList<AIFunctionDeclaration>? remoteMcpDeclarations;
  private IChatClient? chatClient;
  private readonly ChatGptSocketRun? socketRun;
  private readonly SessionToolExecution toolExecution;
  private readonly AIFunctionDeclaration? answerAgentDeclaration;
  private readonly PendingToolCalls pending;
  private readonly ConversationRunner auxiliary;
  private Task? titleWork;
  private int titleFinished;
  private Guid? automaticBoundary;
  private RequestContextEstimate? contextEstimate;
  private readonly CancellationTokenSource stop = new();
  private Task? stopCallbacks;
  private readonly ManualCompactionRequest? manualCompactionRequest;
  private readonly string? compactionSkipReason;
  private const string NoCompactionCut = "No older history can be compacted without dropping recent context.";
  private const string UnexpectedCompactionError = "History compaction failed unexpectedly. Previous context kept.";

  internal SessionExecution(SessionExecutionRecorder recorder, SessionBase session,
      Func<ProviderConnection, IChatClient> createChatClient, LlmSelection selection,
      StartingContextBuilder startingContextBuilder, IReadOnlyList<AgentTool>? tools = null,
      PendingToolCalls? pendingToolCalls = null, ManualCompactionRequest? compaction = null, string? compactionSkipReason = null,
      IRemoteMcpTools? remoteMcpTools = null) {
    this.recorder = recorder;
    this.session = session;
    this.createChatClient = createChatClient;
    this.selection = selection;
    socketRun = selection.Connection.Route == ProviderRoute.ChatGpt ? new ChatGptSocketRun() : null;
    this.startingContextBuilder = startingContextBuilder;
    this.remoteMcpTools = remoteMcpTools;
    maxRemoteToolCount = Math.Max(0, 128 - (tools?.Count ?? 0));
    pending = pendingToolCalls ?? new PendingToolCalls();
    toolExecution = new SessionToolExecution(session, recorder, tools ?? [], pending, selection.SupportsImages, stop.Token);
    if (tools?.OfType<AnswerAgentTool>().SingleOrDefault() is { } answerTool)
      answerAgentDeclaration = AgentToolDeclaration.Create(answerTool);
    auxiliary = new(recorder, createChatClient, tools ?? [], session.WorkingDirectory);
    manualCompactionRequest = compaction;
    this.compactionSkipReason = compactionSkipReason;
    IsCompacting = compaction is not null;
  }

  internal ExecutionIdentity Identity => recorder.Identity;
  internal SessionExecutionRecorder Recorder => recorder;
  internal Guid ExecutionId => Identity.ExecutionId;
  internal bool IsStopping => stop.IsCancellationRequested;
  internal bool IsManualCompaction => manualCompactionRequest is not null;
  internal bool IsCompacting { get; set; }
  internal RequestContextEstimate? ContextEstimate => Volatile.Read(ref contextEstimate);
  internal void RetireContextEstimate() => Volatile.Write(ref contextEstimate, null);
  internal bool HasPendingTools => toolExecution.HasRunningBackgroundTools;
  internal bool HasPendingAuxiliary => titleWork is not null && Volatile.Read(ref titleFinished) == 0;
  internal Task JoinOwnedWorkAsync() => Task.WhenAll(toolExecution.JoinAsync(),
      titleWork ?? Task.CompletedTask, recorder.JoinImageImportsAsync());

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

  // The driver disposes only after all owned work and Stop callbacks have finished.
  public void Dispose() {
    CloseSocket();
    stop.Dispose();
  }

  internal void CloseSocket() => socketRun?.Dispose();

  // Called sequentially outside the session lock. Foreground calls join here;
  // registered background calls remain owned until the session drains them.
  internal async Task<ExecutionOutcome> RunRequestAsync(LlmRequest request) {
    (LlmRequestResult result, IReadOnlyList<AIFunctionDeclaration> offeredTools, ContextStamp? stamp) =
        await SendLlmRequestAsync(request);
    // Retry only the rejected model exchange, never a tool or any observed partial output.
    bool canCompactAndRetry = result.Failure == ProviderFailure.ContextWindowExceeded
        && !result.OutputObserved && !IsStopping;
    if (canCompactAndRetry && await recorder.CanRetryOverflowAsync(request.RequestId)) {
      RetireContextEstimate();
      try {
        LlmRequest? reducedRequest = await CompactHistoryAsync(request.RequestId, CompactionReason.Overflow,
            stamp: request.Stamp);
        if (reducedRequest is null)
          result = result with { Error = $"{NoCompactionCut} Shorten the input or use a larger-context model. ({result.Error})" };
        else
          (result, offeredTools, stamp) = await SendLlmRequestAsync(reducedRequest, allowAutomaticCompaction: false);
      } catch (Exception failure) {
        result = CreateRequestResult(request.RequestId, failure);
      }
    }

    bool requestSaved = await recorder.CompleteRequestAsync(result);
    if (!requestSaved)
      return ExecutionOutcome.Cancelled;
    if (result.Outcome != ExecutionOutcome.Completed)
      return result.Outcome;

    return await toolExecution.RunBatchAsync(request.RequestId, result.ToolCalls ?? [], offeredTools, stamp);
  }

  internal async Task<ExecutionOutcome> RunCompactionAsync() {
    try {
      if (compactionSkipReason is not null)
        await recorder.RecordCompactionErrorAsync(compactionSkipReason);
      else if (await CompactHistoryAsync(null, CompactionReason.Manual, manualCompactionRequest!.Focus) is null)
        await recorder.RecordCompactionErrorAsync(NoCompactionCut);
      return IsStopping ? ExecutionOutcome.Cancelled : ExecutionOutcome.Completed;
    } catch (OperationCanceledException) when (IsStopping) {
      return ExecutionOutcome.Cancelled;
    } catch (CompactionSaveException) {
      throw;
    } catch (Exception failure) {
      await recorder.RecordCompactionErrorAsync(failure is CompactionFailureException
          ? failure.Message : UnexpectedCompactionError);
      return ExecutionOutcome.Failed;
    }
  }

  // Summary work runs outside the session lock. The commit rechecks Stop, revision and prior boundary.
  private async Task<LlmRequest?> CompactHistoryAsync(Guid? requestId, CompactionReason reason, string? focus = null,
      ModelHistory? currentHistory = null, ContextStamp? stamp = null) {
    HistoryCompactionPlan? plan = await CreateCompactionPlanAsync(requestId, reason, focus, currentHistory);
    if (plan is null)
      return null;
    await recorder.SetCompactingAsync(true);
    try {
      GeneratedCompactionSummary result = await GenerateHistorySummaryAsync(plan);
      stop.Token.ThrowIfCancellationRequested();
      bool saved;
      try {
        saved = await recorder.SaveCompactionAsync(plan, result);
      } catch (Exception failure) {
        throw new CompactionSaveException(failure);
      }
      if (!saved)
        throw new OperationCanceledException("The compaction boundary was rejected.");
      socketRun?.RetireConnection();
      ModelHistory compactedHistory = await recorder.ReadModelHistoryAsync(requestId);
      if (reason != CompactionReason.Manual)
        automaticBoundary = compactedHistory.PreviousCompaction?.Id;
      // The claim survives compaction; only the effective boundary moves.
      return new(requestId ?? Guid.Empty, compactedHistory.ReadMessages(), compactedHistory,
          stamp is null ? null : stamp with { CompactionId = compactedHistory.PreviousCompaction?.Id });
    } finally {
      await recorder.SetCompactingAsync(IsManualCompaction);
    }
  }

  private async Task<HistoryCompactionPlan?> CreateCompactionPlanAsync(Guid? requestId, CompactionReason reason,
      string? focus, ModelHistory? currentHistory) {
    stop.Token.ThrowIfCancellationRequested();
    UserPreferences preferences = session.ReadPreferences();
    ModelHistory history = currentHistory ?? await recorder.ReadModelHistoryAsync(requestId);
    StartingContext currentContext = startingContext ?? session.ReadStartingContext()
        ?? startingContextBuilder.Create(ContextInput(), stop.Token);
    HistoryCompactionPlan? plan = HistoryCompactor.CreatePlan(history, selection, currentContext, reason, focus,
        preferences.CompactionTriggerTokenCount);
    return plan is null ? null : plan with {
      BackgroundToolCalls = await recorder.ReadCompactionBackgroundCallsAsync(plan.FirstRetainedMessage.Location)
    };
  }

  private async Task<GeneratedCompactionSummary> GenerateHistorySummaryAsync(HistoryCompactionPlan plan) {
    try {
      LlmSelection? small = await session.ResolveCompactionModelAsync(stop.Token);
      LlmSelection summarySelection = small ?? selection;
      IChatClient summaryClient = small is null
          ? chatClient ??= createChatClient(selection.Connection)
          : createChatClient(summarySelection.Connection);
      return await HistoryCompactor.GenerateSummaryAsync(summaryClient, summarySelection, plan, stop.Token,
          new UsageContext(session.Id, ExecutionId, "compaction"));
    } catch (OperationCanceledException) when (IsStopping) {
      throw;
    } catch (Exception failure) {
      string error = failure switch {
        ProviderOperationException { Failure: ProviderFailure.ContextWindowExceeded } =>
            $"History compaction failed: The summarization request exceeded the model's context window. Try a larger-context model. ({failure.Message})",
        ProviderOperationException or CompactionSummaryException => $"History compaction failed: {failure.Message}",
        _ => UnexpectedCompactionError
      };
      // A failed summary is not a rejection of the conversation request and must not trigger overflow recovery.
      throw new CompactionFailureException(error, failure);
    }
  }

  private StartingContextInput ContextInput() => new(session.Kind, selection.Family, session.WorkingDirectory);

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
            .Select(message => PrepassPrompt(message.Message)).ToArray()
        : request.Messages.Where(message => message.Role == ChatRole.User).Select(PrepassPrompt).ToArray();
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
        captured, protectedTools, pruner, stop.Token, session.ReadInheritedSelection());
    stop.Token.ThrowIfCancellationRequested();
  }

  private static string PrepassPrompt(ChatMessage message) => string.IsNullOrWhiteSpace(message.Text)
      && message.Contents.Any(content => content is DataContent) ? "Image attachment" : message.Text;

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

  // Missing or unavailable choices use the caller's non-model fallback, never the chat model.
  private async Task<AuxiliaryModelResolution> ResolvePrepassModelAsync(AuxiliaryModelPurpose purpose,
      UserPreferences captured, CancellationToken cancellation) =>
      new(await session.ResolveAuxiliaryModelAsync(purpose, captured, cancellation), false);

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

  private async Task PrepareRemoteMcpToolsAsync() {
    if (remoteMcpTools is null || remoteMcpDeclarations is not null)
      return;
    IReadOnlyList<SavedMcpTool> discovered = await remoteMcpTools.DiscoverAsync(stop.Token);
    var tools = new List<RemoteMcpAgentTool>();
    var names = new HashSet<string>(StringComparer.Ordinal);
    int remainingSchema = 128 * 1024;
    foreach (SavedMcpTool saved in discovered) {
      if (tools.Count >= maxRemoteToolCount)
        break;
      if (string.IsNullOrWhiteSpace(saved.Tool.Name) || !RemoteMcpAgentTool.HasSupportedSchema(saved.Tool.InputSchema))
        continue;
      int size = saved.Tool.InputSchema.GetRawText().Length;
      if (size > 16 * 1024 || size > remainingSchema)
        continue;
      var tool = new RemoteMcpAgentTool(remoteMcpTools, saved);
      if (!names.Add(tool.Name))
        continue;
      tools.Add(tool);
      remainingSchema -= size;
    }
    remoteMcpDeclarations = tools.Select(AgentToolDeclaration.Create).ToArray();
    if (tools.Count > 0)
      toolExecution.AddRemoteTools(tools);
  }

  private async Task<(LlmRequestResult Result, IReadOnlyList<AIFunctionDeclaration> OfferedTools, ContextStamp? Stamp)> SendLlmRequestAsync(
      LlmRequest request, bool allowAutomaticCompaction = true) {
    IChatClient client;
    IReadOnlyList<ChatMessage> messages;
    IReadOnlyList<AIFunctionDeclaration> offeredTools;
    ChatOptions options;
    try {
      stop.Token.ThrowIfCancellationRequested();
      if (allowAutomaticCompaction)
        request = await CompactHistoryAsync(request.RequestId, CompactionReason.Threshold,
            currentHistory: request.Context, stamp: request.Stamp) ?? request;
      await PrepareStartingContextAsync(request);
      await PrepareRemoteMcpToolsAsync();
      client = chatClient ??= createChatClient(selection.Connection);
      StartingContext context = startingContext!;
      await StartRequestAsync(request.RequestId, context);
      (bool presented, ChatMessage? reminder) = session.Questions.ReadQuestionForRequest(request);
      // Delegation policy can change between requests without rebuilding the saved starting context.
      // Remote tools are a live execution snapshot, not part of the persisted starting context.
      offeredTools = remoteMcpDeclarations is { Count: > 0 } remote ? [.. context.Tools, .. remote] : context.Tools;
      if (offeredTools.Any(tool => tool.Name == SubagentTool.ToolName)
          && !await session.CanDelegateAsync(stop.Token))
        offeredTools = offeredTools.Where(tool => tool.Name != SubagentTool.ToolName).ToArray();
      if (presented && answerAgentDeclaration is { } answer && offeredTools.All(tool => tool.Name != answer.Name))
        offeredTools = [.. offeredTools, answer];
      options = new() {
        ModelId = selection.ModelId,
        Reasoning = selection.ReadReasoningEffort() is { } effort
            ? new ReasoningOptions { Effort = effort } : null,
        Tools = [.. offeredTools],
        AdditionalProperties = new() {
          [UsageContext.PropertyName] = new UsageContext(session.Id, ExecutionId, "conversation"),
          [OpenAIInferenceClient.SessionIdProperty] = session.Id
        }
      };
      if (socketRun is not null)
        options.AdditionalProperties[ChatGptSocketRun.PropertyName] = socketRun;
      List<ChatMessage> input = [.. context.Instructions, .. request.Messages];
      if (reminder is not null)
        input.Add(reminder);
      messages = LlmMessageProjection.AdaptForModel(input, selection.SupportsImages);
    } catch (Exception failure) {
      return (CreateRequestResult(request.RequestId, failure), [], null);
    }

    // The sent request's stamp: the storage claim, rebased when compaction re-read the context.
    ContextStamp? stamp = request.Stamp;

    await using RequestContextEstimate? estimate = RequestContextEstimate.SupportsModel(selection.ModelId)
        ? new(recorder.PublishContextEstimateAsync) : null;
    if (estimate is not null) {
      Volatile.Write(ref contextEstimate, estimate);
      options.AdditionalProperties[RequestContextEstimate.PropertyName] = estimate;
    }
    LlmResponseRunner.Result response = await LlmResponseRunner.RunAsync(client, messages, options,
        request.RequestId, SaveChunkAsync,
        (callId, name) => RecordToolCallAsync(request.RequestId, callId, name), stop.Token, contextEstimate: estimate);
    LlmRequestResult result = CreateRequestResult(request.RequestId, response.Failure);
    return (result with {
      ToolCalls = result.Outcome == ExecutionOutcome.Completed ? response.ToolCalls : null,
      ProviderResponse = result.Outcome == ExecutionOutcome.Completed ? response.ProviderResponse : null,
      Usage = response.Usage,
      OutputObserved = response.OutputObserved
    }, offeredTools, stamp);
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

  private sealed class CompactionFailureException(string message, Exception inner) : Exception(message, inner);

  private sealed class CompactionSaveException(Exception inner) : Exception("Saving history compaction failed.", inner);

  private sealed class OutputSaveException(Exception inner) : Exception("Saving response output failed.", inner);

  private sealed class RequestStartSaveException(Exception inner) : Exception("Saving the request attempt failed.", inner);
#pragma warning restore S3871
}
