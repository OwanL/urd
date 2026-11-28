using System.Text;
using Microsoft.Extensions.AI;
using Urd.Core.Execution;
using Urd.Core.Preferences;
using Urd.Core.Providers;

namespace Urd.Core.Context;

// Asks an auxiliary model which unprotected starting resources a task needs, one category at a time,
// in an isolated conversation. A failed selection falls back instead of failing the session.
internal sealed class StartingResourcePruner(
    ConversationRunner runner,
    ResolvePruningModel resolveModel,
    SavePruningProgress saveProgress,
    string taskInput) {
  private static readonly PruningCategory<DiscoveredInstructionDocument> Instructions = new(
      "instructions", AuxiliaryModelPurpose.Instructions, InstructionPruningGuidance.Text,
      Identity: document => document.Identity,
      Resource: document => new(document.Identity, Path.GetFileName(document.Path), document.Path),
      Manifest: PruningManifest.ForInstructions) {
    ModelIdPrefix = 'i',
    Size = document => Encoding.UTF8.GetByteCount(document.Text)
  };

  private static readonly PruningCategory<DiscoveredSkill> Skills = new(
      "skills", AuxiliaryModelPurpose.Skills, SkillPruningGuidance.Text,
      Identity: skill => skill.Identity,
      Resource: skill => new(skill.Identity, skill.Name, skill.Path),
      Manifest: PruningManifest.ForSkills) {
    ModelIdPrefix = 's'
  };

  private static readonly PruningCategory<AIFunctionDeclaration> Tools = new(
      "tools", AuxiliaryModelPurpose.Tools, ToolPruningGuidance.Text,
      Identity: tool => tool.Name,
      Resource: tool => new(tool.Name, tool.Name),
      Manifest: PruningManifest.ForTools) {
    RetainsAllOnFailure = true
  };

  private readonly ConversationRunner runner = runner ?? throw new ArgumentNullException(nameof(runner));
  private readonly ResolvePruningModel resolveModel = resolveModel ?? throw new ArgumentNullException(nameof(resolveModel));
  private readonly SavePruningProgress saveProgress = saveProgress ?? throw new ArgumentNullException(nameof(saveProgress));
  private readonly string taskInput = taskInput ?? throw new ArgumentNullException(nameof(taskInput));

  internal Task<StartingResourcePruningResult<DiscoveredInstructionDocument>> PruneAsync(
      IReadOnlyList<DiscoveredInstructionDocument> candidates, int protectedCount, CancellationToken cancellation,
      string? indexWarning = null) => PruneAsync(Instructions, candidates, protectedCount, indexWarning, cancellation);

  internal Task<StartingResourcePruningResult<DiscoveredSkill>> PruneAsync(
      IReadOnlyList<DiscoveredSkill> candidates, int protectedCount, CancellationToken cancellation,
      string? indexWarning = null) => PruneAsync(Skills, candidates, protectedCount, indexWarning, cancellation);

  internal Task<StartingResourcePruningResult<AIFunctionDeclaration>> PruneAsync(
      IReadOnlyList<AIFunctionDeclaration> candidates, int protectedCount, CancellationToken cancellation) =>
      PruneAsync(Tools, candidates, protectedCount, null, cancellation);

  internal static int EstimateManifestTokens(IReadOnlyList<DiscoveredSkill> candidates) =>
      EstimateManifestTokens(Skills, candidates);

  internal static int EstimateManifestTokens(IReadOnlyList<DiscoveredInstructionDocument> candidates) =>
      EstimateManifestTokens(Instructions, candidates);

  private async Task<StartingResourcePruningResult<T>> PruneAsync<T>(PruningCategory<T> category,
      IReadOnlyList<T> candidates, int protectedCount, string? indexWarning, CancellationToken cancellation) {
    ArgumentNullException.ThrowIfNull(candidates);
    ArgumentOutOfRangeException.ThrowIfNegative(protectedCount);
    PruningCandidate<T>[] offered = category.ToCandidates(candidates);
    RequireUniqueIdentities(offered);
    var progress = new PruningProgress(saveProgress, category.Name, protectedCount,
        candidates.Select(category.Resource).ToArray(), indexWarning);

    try {
      cancellation.ThrowIfCancellationRequested();
      await progress.SaveRunningAsync();
    } catch (OperationCanceledException) when (cancellation.IsCancellationRequested) {
      await progress.SaveCancelledAsync();
      throw;
    }

    HashSet<string> keptIds;
    try {
      keptIds = await SelectAsync(category, offered, progress, cancellation);
      cancellation.ThrowIfCancellationRequested();
    } catch (Exception failure) when (cancellation.IsCancellationRequested) {
      await progress.SaveCancelledAsync();
      if (failure is OperationCanceledException or AuxiliaryExecutionException)
        throw;
      throw new OperationCanceledException(cancellation);
    } catch (Exception failure) when (failure is not AuxiliaryExecutionException) {
      // Recording failures are fatal; any other selection failure falls back.
      return await FallBackAsync(category, offered, progress, failure);
    }

    PruningCandidate<T>[] kept = offered.Where(candidate => keptIds.Contains(candidate.ModelId)).ToArray();
    await progress.SaveCompletedAsync(kept.Select(candidate => candidate.Identity).ToArray());
    return new(Array.AsReadOnly(kept.Select(candidate => candidate.Item).ToArray()));
  }

  // Returns the model ids the selector kept. Categories with a size retry a context overflow without
  // their largest candidates, doubling how many are dropped each time; the last one is always tried alone.
  private async Task<HashSet<string>> SelectAsync<T>(PruningCategory<T> category,
      PruningCandidate<T>[] candidates, PruningProgress progress, CancellationToken cancellation) {
    cancellation.ThrowIfCancellationRequested();
    if (candidates.Length == 0)
      return [];

    AuxiliaryModelResolution model = await resolveModel(category.Purpose, cancellation);
    PruningCandidate<T>[] submitted = candidates;
    int dropCount = 1;
    while (true) {
      cancellation.ThrowIfCancellationRequested();
      try {
        string answer = await runner.RunAsync($"pruning.{category.Name}", model,
            SelectorInput(category, submitted), null, cancellation, retryServiceUnavailable: true);
        cancellation.ThrowIfCancellationRequested();
        return PruningAnswer.ReadKeptIds(answer, submitted.Select(candidate => candidate.ModelId));
      } catch (ProviderOperationException failure) when (category.Size is { } size
          && failure.Failure == ProviderFailure.ContextWindowExceeded) {
        cancellation.ThrowIfCancellationRequested();
        int dropping = submitted.Length == 1 ? 1 : Math.Min(dropCount, submitted.Length - 1);
        submitted = submitted.Except(submitted.OrderByDescending(candidate => size(candidate.Item)).Take(dropping))
            .ToArray();
        progress.RecordSkipped(dropping);
        if (submitted.Length == 0)
          throw new InvalidDataException("Instruction pruning exceeded the context window with one candidate remaining; all unprotected candidates were excluded.", failure);
        dropCount = dropping * 2;
        await progress.SaveRetryAsync();
      }
    }
  }

  private static async Task<StartingResourcePruningResult<T>> FallBackAsync<T>(PruningCategory<T> category,
      PruningCandidate<T>[] candidates, PruningProgress progress, Exception failure) {
    PruningCandidate<T>[] retained = category.RetainsAllOnFailure ? candidates : [];
    string retainedSummary = category.RetainsAllOnFailure
        ? $"Retained all unprotected {category.Name} and protected resources."
        : "Retained only protected resources.";
    string error = await progress.SaveFallbackAsync(failure, retainedSummary,
        retained.Select(candidate => candidate.Identity).ToArray());
    return new(Array.AsReadOnly(retained.Select(candidate => candidate.Item).ToArray()), IsFallback: true, Error: error);
  }

  private ChatMessage[] SelectorInput<T>(PruningCategory<T> category, PruningCandidate<T>[] candidates) => [
    new(ChatRole.System, $"{category.Guidance}\n{SharedPruningGuidance.Text}"),
    new(ChatRole.User, $"""
        Task:
        {taskInput}

        Candidates:
        {PruningManifest.Serialize(category.Manifest(candidates))}
        """)
  ];

  private static int EstimateManifestTokens<T>(PruningCategory<T> category, IReadOnlyList<T> candidates) =>
      candidates.Count == 0 ? 0 : PruningManifest.EstimateTokens(category.Manifest(category.ToCandidates(candidates)));

  private static void RequireUniqueIdentities<T>(PruningCandidate<T>[] candidates) {
    if (candidates.Any(candidate => string.IsNullOrWhiteSpace(candidate.Identity))
        || candidates.DistinctBy(candidate => candidate.Identity, StringComparer.Ordinal).Count() != candidates.Length)
      throw new ArgumentException("Pruning candidate identities must be nonempty and unique.", nameof(candidates));
  }
}
