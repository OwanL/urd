using Microsoft.Extensions.AI;
using Urd.Core.Context;
using Urd.Core.Providers;
using Urd.Core.Usage;

namespace Urd.Core.Compaction;

internal static class HistoryCompactor {
  internal const int MaxSummaryCharacters = 32_768;

  internal static HistoryCompactionPlan? CreatePlan(ModelHistory history, LlmSelection selection,
      StartingContext startingContext, CompactionReason reason, string? focus = null,
      int? compactionTriggerTokenCount = null) {
    ArgumentNullException.ThrowIfNull(history);
    ArgumentNullException.ThrowIfNull(selection);
    ArgumentNullException.ThrowIfNull(startingContext);
    ArgumentNullException.ThrowIfNull(startingContext.Instructions);
    ArgumentNullException.ThrowIfNull(startingContext.Tools);
    if (!Enum.IsDefined(reason)) throw new ArgumentOutOfRangeException(nameof(reason));
    if (history.Messages.Count < 2) return null;

    long estimatedContextTokens = EstimateFullContext(history, startingContext);
    int? trigger = ContextTokenBudget.CompactionTrigger(selection.ContextWindow, compactionTriggerTokenCount);
    if (reason == CompactionReason.Threshold && !ExceedsCompactionTrigger(history, trigger, estimatedContextTokens))
      return null;

    int retainedHistoryTokenLimit = ContextTokenBudget.RetainedHistoryLimit(trigger);
    int? firstRetainedMessageIndex = HistoryCompactionBoundary.FindFirstRetainedMessageIndex(history, retainedHistoryTokenLimit, reason);
    return firstRetainedMessageIndex is { } index ? new(history, index, estimatedContextTokens, reason, focus) : null;
  }

  internal static async Task<GeneratedCompactionSummary> GenerateSummaryAsync(IChatClient client, LlmSelection selection,
      HistoryCompactionPlan plan, CancellationToken cancellationToken, UsageContext? usageContext = null) {
    ArgumentNullException.ThrowIfNull(client);
    ArgumentNullException.ThrowIfNull(selection);
    ArgumentNullException.ThrowIfNull(plan);
    ValidatePlan(plan);
    cancellationToken.ThrowIfCancellationRequested();

    var options = new ChatOptions {
      ModelId = selection.ModelId,
      // ChatGPT's subscription endpoint rejects max_output_tokens; its summaries rely on the model's default ceiling.
      MaxOutputTokens = selection.Connection.Route == ProviderRoute.ChatGpt
          ? null : ContextTokenBudget.SummaryOutputLimit(selection.ContextWindow),
      Tools = []
    };
    if (usageContext is not null)
      options.AdditionalProperties = new() { [UsageContext.PropertyName] = usageContext };
    ChatResponse response = await client.GetResponseAsync(
        [new ChatMessage(ChatRole.User, CompactionPrompt.Build(plan))], options, cancellationToken).ConfigureAwait(false);
    cancellationToken.ThrowIfCancellationRequested();

    string summary = ReadCompletedSummary(response);
    long estimatedContextTokensAfter = EstimateCompactedContext(plan, plan.AppendBackgroundToolCalls(summary));
    if (estimatedContextTokensAfter >= plan.EstimatedContextTokensBefore)
      throw new InvalidDataException("The compaction summary would not reduce the estimated context.");

    cancellationToken.ThrowIfCancellationRequested();
    return new(summary, estimatedContextTokensAfter);
  }

  private static bool ExceedsCompactionTrigger(ModelHistory history, int? trigger, long estimatedContextTokens) {
    if (trigger is null) return false;
    long contextTokens = history.UsageBasedContextTokens is > 0 ? history.UsageBasedContextTokens.Value : estimatedContextTokens;
    return contextTokens > trigger.Value;
  }

  private static string ReadCompletedSummary(ChatResponse response) {
    if (response.FinishReason != ChatFinishReason.Stop || response.Messages.Any(message =>
        message.Contents.Any(content => content is FunctionCallContent)))
      throw new InvalidDataException("The compaction summary did not finish successfully.");

    string summary = string.Concat(response.Messages.Where(message => message.Role == ChatRole.Assistant)
        .SelectMany(message => message.Contents.OfType<TextContent>().Select(content => content.Text))).Trim();
    if (summary.Length == 0 || summary.Length > MaxSummaryCharacters)
      throw new InvalidDataException("The compaction summary was empty or exceeded its size limit.");
    return summary;
  }

  private static long EstimateCompactedContext(HistoryCompactionPlan plan, string summary) {
    long historyTokensBefore = ContextTokenBudget.EstimateMessages(plan.History.EnumerateMessages());
    if (plan.EstimatedContextTokensBefore < historyTokensBefore)
      throw new InvalidDataException("The compaction plan has an invalid token boundary.");

    long instructionAndToolTokens = plan.EstimatedContextTokensBefore - historyTokensBefore;
    long summaryTokens = ContextTokenBudget.EstimateMessage(ModelHistory.SummaryMessage(summary));
    long retainedHistoryTokens = ContextTokenBudget.EstimateMessages(plan.History.Messages
        .Skip(plan.FirstRetainedMessageIndex).Select(message => message.Message));
    return ContextTokenBudget.AddSaturating(instructionAndToolTokens,
        ContextTokenBudget.AddSaturating(summaryTokens, retainedHistoryTokens));
  }

  private static long EstimateFullContext(ModelHistory history, StartingContext startingContext) {
    long total = ContextTokenBudget.EstimateMessages(startingContext.Instructions.Concat(history.EnumerateMessages()));
    foreach (AIFunctionDeclaration tool in startingContext.Tools) {
      total = ContextTokenBudget.AddSaturating(total, ContextTokenBudget.EstimateText(tool.Name));
      total = ContextTokenBudget.AddSaturating(total, ContextTokenBudget.EstimateText(tool.Description));
      total = ContextTokenBudget.AddSaturating(total, ContextTokenBudget.EstimateText(tool.JsonSchema.GetRawText()));
    }
    return total;
  }

  private static void ValidatePlan(HistoryCompactionPlan plan) {
    IReadOnlyList<HistoryMessage> messages = plan.History.Messages;
    if (messages.Count < 2 || plan.FirstRetainedMessageIndex <= 0 || plan.FirstRetainedMessageIndex >= messages.Count ||
        plan.EstimatedContextTokensBefore <= 0)
      throw new ArgumentException("The compaction plan has no safe prefix and suffix.", nameof(plan));
    HistoryMessage firstRetained = plan.FirstRetainedMessage;
    if (!firstRetained.IsUserTurnStart && firstRetained.Message.Role != ChatRole.Assistant)
      throw new ArgumentException("The compaction plan starts at an unsafe history boundary.", nameof(plan));
    if (plan.History.PreviousCompaction is { } previous && !firstRetained.Location.IsAfter(previous.FirstRetainedMessageLocation))
      throw new ArgumentException("The compaction plan does not advance the existing boundary.", nameof(plan));
  }
}
