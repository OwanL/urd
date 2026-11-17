using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Urd.Core.Providers;
using Urd.Core.Tools;
using Urd.Core.Usage;

namespace Urd.Core.Execution;

// One isolated tool-capable auxiliary conversation loop. It reuses LlmResponseRunner,
// ToolInvoker and the parent's scoped AuxiliaryConversationRecorder, but owns no session
// admission, stopping, settlement or pending-tool registry; the caller owns the run task
// and must join it before the parent execution settles.
internal sealed class ConversationRunner(SessionExecutionRecorder parent,
    Func<ProviderConnection, IChatClient> createChatClient, IReadOnlyList<AgentTool> tools,
    string? workingDirectory) {
  // Runs one auxiliary conversation and returns its accepted final answer. validateResult
  // returns null for an accepted answer or corrective user input; one corrected final
  // answer is permitted (tool turns are not counted), a second rejected one throws.
  internal async Task<string> RunAsync(string purpose, AuxiliaryModelResolution model,
      IReadOnlyList<ChatMessage> input, Func<string, string?>? validateResult, CancellationToken cancellation) {
    try {
      return await RunCoreAsync(purpose, model, input, validateResult, cancellation);
    } catch (AuxiliaryExecutionException failure) {
      await parent.ReportAuxiliaryFailureAsync(failure);
      throw;
    }
  }

  private async Task<string> RunCoreAsync(string purpose, AuxiliaryModelResolution model,
      IReadOnlyList<ChatMessage> input, Func<string, string?>? validateResult, CancellationToken cancellation) {
    ArgumentException.ThrowIfNullOrWhiteSpace(purpose);
    ArgumentNullException.ThrowIfNull(model);
    ArgumentNullException.ThrowIfNull(input);
    LlmSelection selection = model.Selection;
    selection.Validate();
    cancellation.ThrowIfCancellationRequested();

    // Auxiliary runs admit exactly read and bash, foreground only. Definitions must stay
    // identical to ToolInvoker's expectation, so capability filtering keeps full declarations.
    AgentTool[] allowed = [.. tools.Where(tool => tool.Name is "read" or "bash")];
    AIFunctionDeclaration[] offered = [.. allowed.Select(AgentToolDeclaration.Create)];
    List<ChatMessage> messages = [.. input]; // Isolated copy; the caller's history is never mutated.
    AuxiliaryConversationRecorder recorder = await CreateConversationAsync(purpose, model, messages, offered, cancellation);
    IChatClient client = createChatClient(selection.Connection); // Borrowed; disposal stays with its owner.
    int answers = 0;

    while (true) {
      cancellation.ThrowIfCancellationRequested();
      Guid requestId = Guid.NewGuid();
      var text = new StringBuilder();
      // The request is durable before any provider contact and before any tool result.
      await RequireAcceptedAsync(recorder.StartRequestAsync(requestId), cancellation);
      LlmResponseRunner.Result response = await StreamResponseAsync(recorder, client, purpose, selection,
          messages, offered, requestId, text, cancellation);
      Exception? failure = response.Failure;

      if (failure is null) {
        await RequireAcceptedAsync(recorder.CompleteRequestAsync(new(requestId, ExecutionOutcome.Completed,
            ToolCalls: response.ToolCalls, ProviderResponse: response.ProviderResponse, Usage: response.Usage)), cancellation);
        ChatMessage? assistant = ProjectAssistantMessage(response, text);
        if (assistant is not null)
          messages.Add(assistant);

        if (response.ToolCalls.Count == 0) {
          string answer = text.ToString();
          string? correction = validateResult?.Invoke(answer);
          if (correction is null)
            return answer;
          if (answers++ != 0)
            throw new InvalidDataException("The auxiliary response failed validation after its one correction.");
          messages.Add(new(ChatRole.User, correction));
          continue;
        }

        var results = new List<AIContent>(response.ToolCalls.Count);
        foreach (LlmToolCall call in response.ToolCalls) {
          // Claim durably before invoking; every result is saved before any continuation.
          await RequireAcceptedAsync(recorder.TryStartToolAsync(requestId, call.CallId), cancellation);
          LlmToolResult result = await InvokeToolAsync(requestId, call, allowed, offered, selection, cancellation);
          await RequireAcceptedAsync(recorder.CompleteToolAsync(requestId, result), cancellation);
          results.Add(ProjectResult(result));
          if (result.Outcome == ExecutionOutcome.Cancelled)
            cancellation.ThrowIfCancellationRequested();
        }
        messages.Add(new(ChatRole.Tool, results));
        continue;
      }

      if (failure is AuxiliaryExecutionException)
        ExceptionDispatchInfo.Throw(failure); // Recording faults are fatal, never failed requests.

      ExecutionOutcome outcome = cancellation.IsCancellationRequested
          ? ExecutionOutcome.Cancelled : ExecutionOutcome.Failed;
      await RequireAcceptedAsync(recorder.CompleteRequestAsync(new(requestId, outcome,
          Error: outcome == ExecutionOutcome.Failed ? failure.Message : null,
          Failure: (failure as ProviderOperationException)?.Failure)), cancellation);
      if (failure is OperationCanceledException)
        ExceptionDispatchInfo.Throw(failure);
      cancellation.ThrowIfCancellationRequested();
      ExceptionDispatchInfo.Throw(failure); // Ordinary failures throw normally after safe closing.
    }
  }

  // A scoped durable conversation freezes this run's purpose, selection, input and definitions.
  private async Task<AuxiliaryConversationRecorder> CreateConversationAsync(string purpose, AuxiliaryModelResolution model,
      IReadOnlyList<ChatMessage> input, IReadOnlyList<AIFunctionDeclaration> offered, CancellationToken cancellation) {
    AuxiliaryConversationRecorder? recorder;
    try {
      recorder = await parent.CreateConversationAsync(Guid.NewGuid(), purpose, model, input, offered);
    } catch (ArgumentException) {
      throw; // Caller-invalid input, not a recording fault.
    } catch (Exception failure) {
      throw new AuxiliaryExecutionException("Creating the auxiliary conversation failed.", failure);
    }
    if (recorder is not null)
      return recorder;
    cancellation.ThrowIfCancellationRequested();
    throw new AuxiliaryExecutionException("The auxiliary conversation could not be created.");
  }

  private async Task<LlmResponseRunner.Result> StreamResponseAsync(AuxiliaryConversationRecorder recorder,
      IChatClient client, string purpose, LlmSelection selection, IReadOnlyList<ChatMessage> messages,
      IReadOnlyList<AIFunctionDeclaration> offered, Guid requestId, StringBuilder text, CancellationToken cancellation) {
    ChatOptions options = new() {
      ModelId = selection.ModelId,
      Reasoning = selection.ReadReasoningEffort() is { } effort ? new ReasoningOptions { Effort = effort } : null,
      Tools = [.. offered],
      AdditionalProperties = new() {
        [UsageContext.PropertyName] = new UsageContext(parent.Identity.SessionId, parent.Identity.ExecutionId, purpose)
      }
    };
    IReadOnlyList<ChatMessage> adapted = LlmMessageProjection.AdaptForModel(messages, selection.SupportsImages);
    try {
      return await LlmResponseRunner.RunAsync(client, adapted, options, requestId,
          async chunk => {
            text.Append(chunk.Text);
            await RequireAcceptedAsync(recorder.RecordOutputAsync(chunk), cancellation);
            // Preview capture is advisory; a parent with no active prepass work stays benign.
            await parent.RecordPrepassPreviewAsync(purpose, output: chunk.Text);
          },
          (callId, name) => RequireAcceptedAsync(recorder.RecordToolCallAsync(requestId, callId, name), cancellation),
          cancellation,
          chunk => parent.RecordPrepassPreviewAsync(purpose, reasoning: chunk.Text));
    } catch (AggregateException cleanup) {
      throw new AuxiliaryExecutionException("The auxiliary request could not be cleaned up.", cleanup);
    }
  }

  // Ordinary invocation errors become tool feedback; only cleanup loss is fatal.
  private async Task<LlmToolResult> InvokeToolAsync(Guid requestId, LlmToolCall call, AgentTool[] allowed,
      AIFunctionDeclaration[] offered, LlmSelection selection, CancellationToken cancellation) {
    try {
      return await ToolInvoker.InvokeAsync(requestId, call, allowed, offered, workingDirectory,
          selection.SupportsImages, cancellation);
    } catch (ToolCleanupException cleanup) {
      throw new AuxiliaryExecutionException("The auxiliary tool could not be cleaned up.", cleanup);
    }
  }

  // Native provider context replays as itself; portable responses project text and calls.
  private static ChatMessage? ProjectAssistantMessage(LlmResponseRunner.Result response, StringBuilder text) {
    if (response.ProviderResponse is { } providerResponse)
      return LlmProviderContext.Restore(providerResponse);

    var contents = new List<AIContent>();
    if (text.Length != 0)
      contents.Add(new TextContent(text.ToString()));
    contents.AddRange(response.ToolCalls.Select(call => new FunctionCallContent(call.CallId, call.Name,
        JsonSerializer.Deserialize<Dictionary<string, object?>>(call.Arguments.GetRawText())!)));
    return contents.Count == 0 ? null : new(ChatRole.Assistant, contents);
  }

  // Model-visible results carry text and images; inspection-only details never reach the model.
  private static FunctionResultContent ProjectResult(LlmToolResult result) {
    ToolOutput output = result.Output;
    object content = output.Images is { Count: > 0 }
        ? (AIContent[])[new TextContent(output.Text), .. output.Images]
        : output.Text;
    return new(result.CallId, content);
  }

  // A save exception, or a rejection while the run is not stopping, is a fatal auxiliary fault.
  private static async Task<bool> RequireAcceptedAsync(Task<bool> save, CancellationToken cancellation) {
    bool saved;
    try {
      saved = await save;
    } catch (OperationCanceledException) when (cancellation.IsCancellationRequested) {
      throw;
    } catch (Exception failure) {
      throw new AuxiliaryExecutionException("Saving auxiliary conversation progress failed.", failure);
    }
    if (saved)
      return true;
    cancellation.ThrowIfCancellationRequested();
    throw new AuxiliaryExecutionException("The auxiliary conversation no longer accepts its caller's recording.");
  }
}

#pragma warning disable S3871 // Owned fatal auxiliary fault, not a public exception contract.
// Recording, cleanup or authority loss that must fence its caller instead of becoming
// model feedback or a selection fallback.
internal sealed class AuxiliaryExecutionException(string message, Exception? innerException = null)
    : Exception(message, innerException);
#pragma warning restore S3871
