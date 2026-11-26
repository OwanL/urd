using Urd.Core.Execution;
using Urd.Core.Sessions;

namespace Urd.Core.Context;

// Saves one category's pruning status as it starts, retries, settles or is cancelled.
internal sealed class PruningProgress(
    SavePruningProgress save, string category, int protectedCount,
    IReadOnlyList<PrepassResource> candidates, string? indexWarning) {
  private readonly int total = checked(candidates.Count + protectedCount);
  private int skippedCount;

  // The index warning followed by any skipped-candidate warning.
  internal string? Warning => JoinSentences(indexWarning, SkippedWarning());

  // Records instruction files left out of the selector request after a context overflow.
  internal void RecordSkipped(int count) => skippedCount += count;

  internal Task SaveRunningAsync() => SaveAsync("running", total, Warning);

  internal async Task SaveRetryAsync() {
    try {
      await SaveAsync("running", total - skippedCount, Warning);
    } catch (Exception failure) when (failure is not (OperationCanceledException or AuxiliaryExecutionException)) {
      throw new AuxiliaryExecutionException("Saving the instruction retry warning failed.", failure);
    }
  }

  internal Task SaveCancelledAsync() => SaveAsync("cancelled", protectedCount, Warning);

  internal Task SaveCompletedAsync(IReadOnlyList<string> retainedIds) =>
      SaveAsync("completed", Retained(retainedIds), Warning, retainedIds);

  // Returns the saved error: any warnings, then the failure, then what was retained instead.
  internal async Task<string> SaveFallbackAsync(Exception failure, string retainedSummary, IReadOnlyList<string> retainedIds) {
    string failureSentence = failure.Message.EndsWith('.') ? failure.Message : $"{failure.Message}.";
    string error = JoinSentences(Warning, failureSentence, retainedSummary)!;
    await SaveAsync("fallback", Retained(retainedIds), error, retainedIds);
    return error;
  }

  private Task SaveAsync(string state, int retained, string? error, IReadOnlyList<string>? retainedIds = null) =>
      save(new(category, state, total, retained, error), candidates, retainedIds);

  private int Retained(IReadOnlyList<string> retainedIds) => checked(retainedIds.Count + protectedCount);

  private string? SkippedWarning() {
    if (skippedCount == 0)
      return null;
    string files = skippedCount == 1 ? "file" : "files";
    return $"Skipped {skippedCount} instruction {files} because the context limit was exceeded.";
  }

  private static string? JoinSentences(params string?[] sentences) =>
      sentences.Any(sentence => sentence is not null) ? string.Join(' ', sentences.OfType<string>()) : null;
}
