using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using Urd.Core.Execution;
using Urd.Core.Preferences;
using Urd.Core.Providers;
using Urd.Core.Sessions;

namespace Urd.Core.Context;

// Prunes one resource category in an isolated auxiliary conversation.
internal sealed class StartingResourcePruner(
    ConversationRunner runner,
    Func<AuxiliaryModelPurpose, CancellationToken, Task<AuxiliaryModelResolution>> resolveModel,
    Func<StartingResourcePruningStatus, Task> saveStatus,
    string taskInput,
    Func<StartingResourcePruningStatus, IReadOnlyList<PrepassResource>, IReadOnlyList<string>?, Task>? saveProgress = null) {
  private readonly ConversationRunner runner = runner ?? throw new ArgumentNullException(nameof(runner));
  private readonly Func<AuxiliaryModelPurpose, CancellationToken, Task<AuxiliaryModelResolution>> resolveModel =
      resolveModel ?? throw new ArgumentNullException(nameof(resolveModel));
  private readonly Func<StartingResourcePruningStatus, Task> saveStatus =
      saveStatus ?? throw new ArgumentNullException(nameof(saveStatus));
  private readonly string taskInput = taskInput ?? throw new ArgumentNullException(nameof(taskInput));
  private readonly Func<StartingResourcePruningStatus, IReadOnlyList<PrepassResource>, IReadOnlyList<string>?, Task>? saveProgress = saveProgress;

  internal Task<StartingResourcePruningResult<DiscoveredInstructionDocument>> PruneAsync(
      IReadOnlyList<DiscoveredInstructionDocument> candidates, int protectedCount,
      CancellationToken cancellation, string? indexWarning = null) =>
      PruneAsync(candidates, protectedCount, AuxiliaryModelPurpose.Instructions, "instructions",
          InstructionPruningGuidance.Text,
          indexWarning,
          document => document.Identity,
          documents => InstructionManifest(documents),
          document => new(document.Identity, System.IO.Path.GetFileName(document.Path), document.Path),
          document => Encoding.UTF8.GetByteCount(document.Text), cancellation);

  internal Task<StartingResourcePruningResult<DiscoveredSkill>> PruneAsync(
      IReadOnlyList<DiscoveredSkill> candidates, int protectedCount, CancellationToken cancellation,
      string? indexWarning = null) =>
      PruneAsync(candidates, protectedCount, AuxiliaryModelPurpose.Skills, "skills",
          SkillPruningGuidance.Text,
          indexWarning,
          skill => skill.Identity,
          skills => SkillManifest(skills),
          skill => new(skill.Identity, skill.Name, skill.Path),
          null, cancellation);

  internal Task<StartingResourcePruningResult<AIFunctionDeclaration>> PruneAsync(
      IReadOnlyList<AIFunctionDeclaration> candidates, int protectedCount, CancellationToken cancellation) =>
      PruneAsync(candidates, protectedCount, AuxiliaryModelPurpose.Tools, "tools",
          ToolPruningGuidance.Text, null,
          tool => tool.Name,
          tools => tools.Select(item => new ToolCandidate(item.Id, "tool", item.Item.Name, item.Item.Description, item.Item.JsonSchema)).ToArray(),
          tool => new(tool.Name, tool.Name),
          null, cancellation);

  private async Task<StartingResourcePruningResult<T>> PruneAsync<T>(
      IReadOnlyList<T> candidates,
      int protectedCount,
      AuxiliaryModelPurpose purpose,
      string category,
      string categoryBrief,
      string? indexWarning,
      Func<T, string> getIdentity,
      Func<IEnumerable<(T Item, string Id)>, object[]> createMetadata,
      Func<T, PrepassResource> createResource,
      Func<T, int>? getSize,
      CancellationToken cancellation) {
    ArgumentNullException.ThrowIfNull(candidates);
    ArgumentNullException.ThrowIfNull(getIdentity);
    ArgumentNullException.ThrowIfNull(createMetadata);
    ArgumentNullException.ThrowIfNull(createResource);
    ArgumentOutOfRangeException.ThrowIfNegative(protectedCount);

    string[] identities = candidates.Select(getIdentity).ToArray();
    if (identities.Any(string.IsNullOrWhiteSpace) || identities.Distinct(StringComparer.Ordinal).Count() != identities.Length)
      throw new ArgumentException("Pruning candidate identities must be nonempty and unique.", nameof(candidates));

    string[] modelIds = candidates.Select((_, index) => purpose switch {
      AuxiliaryModelPurpose.Skills => $"s{index + 1}",
      AuxiliaryModelPurpose.Instructions => $"i{index + 1}",
      _ => identities[index]
    }).ToArray();
    PrepassResource[] resources = candidates.Select(createResource).ToArray();
    int total = checked(candidates.Count + protectedCount);
    var running = new StartingResourcePruningStatus(category, "running", total, total, indexWarning);
    try {
      cancellation.ThrowIfCancellationRequested();
      await SaveStatusAsync(running, resources, null);
    } catch (OperationCanceledException) when (cancellation.IsCancellationRequested) {
      await SaveCancelledStatusAsync(category, total, protectedCount, resources, indexWarning);
      throw;
    }

    string[] selectedIds;
    string? warning = indexWarning;
    var excluded = new bool[candidates.Count];
    try {
      cancellation.ThrowIfCancellationRequested();
      if (candidates.Count == 0) {
        selectedIds = [];
      } else {
        AuxiliaryModelResolution model = await resolveModel(purpose, cancellation);
        cancellation.ThrowIfCancellationRequested();
        int[]? largestFirst = null;
        int excludedCount = 0;
        int nextDropCount = 1;
        while (true) {
          string[] submittedIds = modelIds.Where((_, index) => !excluded[index]).ToArray();
          object[] metadata = createMetadata(candidates.Select((item, index) => (Item: item, Id: modelIds[index], Index: index))
              .Where(item => !excluded[item.Index]).Select(item => (item.Item, item.Id)));
          ChatMessage[] input = CreateSelectorInput(categoryBrief, metadata);
          try {
            string answer = await runner.RunAsync($"pruning.{category}", model, input, null, cancellation);
            cancellation.ThrowIfCancellationRequested();
            selectedIds = ReadKeptIds(answer, submittedIds);
            break;
          } catch (ProviderOperationException failure) when (getSize is not null
              && failure.Failure == ProviderFailure.ContextWindowExceeded) {
            cancellation.ThrowIfCancellationRequested();
            largestFirst ??= Enumerable.Range(0, candidates.Count)
                .OrderByDescending(index => getSize(candidates[index])).ThenBy(index => index).ToArray();
            int remaining = candidates.Count - excludedCount;
            int dropCount = remaining == 1 ? 1 : Math.Min(nextDropCount, remaining - 1);
            foreach (int index in largestFirst.Skip(excludedCount).Take(dropCount))
              excluded[index] = true;
            excludedCount += dropCount;
            string skipped = SkippedWarning(excludedCount);
            warning = warning is null ? skipped : $"{warning} {skipped}";
            if (excludedCount == candidates.Count)
              throw new InvalidDataException("Instruction pruning exceeded the context window with one candidate remaining; all unprotected candidates were excluded.", failure);
            nextDropCount = nextDropCount > int.MaxValue / 2 ? int.MaxValue : nextDropCount * 2;
            await SaveRetryAsync(new(category, "running", total, total - excludedCount, warning), resources);
          }
        }
      }
      cancellation.ThrowIfCancellationRequested();
    } catch (OperationCanceledException) when (cancellation.IsCancellationRequested) {
      await SaveCancelledStatusAsync(category, total, protectedCount, resources, warning);
      throw;
    } catch (AuxiliaryExecutionException) {
      if (cancellation.IsCancellationRequested)
        await SaveCancelledStatusAsync(category, total, protectedCount, resources, warning);
      throw;
    } catch (Exception failure) {
      if (cancellation.IsCancellationRequested) {
        await SaveCancelledStatusAsync(category, total, protectedCount, resources, warning);
        throw new OperationCanceledException(cancellation);
      }

      bool keepCandidates = purpose == AuxiliaryModelPurpose.Tools;
      T[] remaining = candidates.Where((_, index) => keepCandidates && !excluded[index]).ToArray();
      string[] remainingIds = identities.Where((_, index) => keepCandidates && !excluded[index]).ToArray();
      string retainedMessage = keepCandidates ? "Retained all unprotected tools and protected resources." : "Retained only protected resources.";
      string failureMessage = failure.Message.EndsWith('.') ? failure.Message : $"{failure.Message}.";
      string error = warning is null ? $"{failureMessage} {retainedMessage}" : $"{warning} {failureMessage} {retainedMessage}";
      await SaveStatusAsync(new(category, "fallback", total, checked(remaining.Length + protectedCount), error),
          resources, remainingIds);
      return new(Array.AsReadOnly(remaining), IsFallback: true, Error: error);
    }

    var selected = new HashSet<string>(selectedIds, StringComparer.Ordinal);
    T[] retained = candidates.Where((_, index) => selected.Contains(modelIds[index])).ToArray();
    string[] resultIds = identities.Where((_, index) => selected.Contains(modelIds[index])).ToArray();
    await SaveStatusAsync(new(category, "completed", total, checked(retained.Length + protectedCount), warning), resources, resultIds);
    return new(Array.AsReadOnly(retained));
  }

  private ChatMessage[] CreateSelectorInput(string categoryBrief, object[] metadata) => [
    new(ChatRole.System, $"{categoryBrief}\n{SharedPruningGuidance.Text}"),
    new(ChatRole.User, $"""
        Task:
        {taskInput}

        Candidates:
        {JsonSerializer.Serialize(metadata)}
        """)
  ];

  private static string[] ReadKeptIds(string answer, IReadOnlyList<string> candidateIds) {
    using JsonDocument document = JsonDocument.Parse(answer);
    JsonElement root = document.RootElement;
    if (root.ValueKind != JsonValueKind.Object)
      throw InvalidResult();

    JsonElement[] keptIdProperties = root.EnumerateObject()
        .Where(property => property.NameEquals("keptIds")).Select(property => property.Value).ToArray();
    if (keptIdProperties is not [{ ValueKind: JsonValueKind.Array } keptIds])
      throw InvalidResult();

    var available = new HashSet<string>(candidateIds, StringComparer.Ordinal);
    var selected = new HashSet<string>(StringComparer.Ordinal);
    foreach (JsonElement value in keptIds.EnumerateArray()) {
      if (value.ValueKind != JsonValueKind.String || value.GetString() is not { } id)
        throw InvalidResult();
      if (available.Contains(id))
        selected.Add(id);
    }
    return selected.ToArray();
  }

  private Task SaveStatusAsync(StartingResourcePruningStatus status, IReadOnlyList<PrepassResource> resources,
      IReadOnlyList<string>? resultIds) => saveProgress is null ? saveStatus(status) : saveProgress(status, resources, resultIds);

  private async Task SaveRetryAsync(StartingResourcePruningStatus status, IReadOnlyList<PrepassResource> resources) {
    try {
      await SaveStatusAsync(status, resources, null);
    } catch (Exception failure) when (failure is not (OperationCanceledException or AuxiliaryExecutionException)) {
      throw new AuxiliaryExecutionException("Saving the instruction retry warning failed.", failure);
    }
  }

  private Task SaveCancelledStatusAsync(string category, int total, int retained,
      IReadOnlyList<PrepassResource> resources, string? warning = null) =>
      SaveStatusAsync(new(category, "cancelled", total, retained, warning), resources, null);

  private static string SkippedWarning(int count) =>
      $"Skipped {count} instruction {(count == 1 ? "file" : "files")} because the context limit was exceeded.";

  private static InvalidDataException InvalidResult() =>
      new("The pruning response must contain exactly one keptIds string array.");

  private const int InlineTextBudget = 16 * 1024;
  private const int MaxInlineTextBytes = 2 * 1024;

  private static object[] InstructionManifest(IEnumerable<(DiscoveredInstructionDocument Item, string Id)> candidates) {
    var items = candidates.ToArray();
    var text = AllocateText(items.Select(item => (item.Id, item.Item.Text)), "Content", "ContentRef", referenceOmitted: true);
    return items.GroupBy(item => System.IO.Path.GetDirectoryName(item.Item.Path) ?? "")
        .Select(group => new DirectoryCandidates(group.Key, group.Select(item => {
          var inline = text[item.Id];
          return (object)new InstructionCandidate(item.Id, System.IO.Path.GetFileName(item.Item.Path),
              Encoding.UTF8.GetByteCount(item.Item.Text), item.Item.Scope, inline.Text, inline.Reference);
        }).ToArray())).ToArray();
  }

  private static object[] SkillManifest(IEnumerable<(DiscoveredSkill Item, string Id)> candidates) {
    var items = candidates.ToArray();
    var text = AllocateText(items.Select(item => (item.Id, item.Item.Description)), "Description", "DescriptionRef", referenceOmitted: false);
    return items.GroupBy(item => System.IO.Path.GetDirectoryName(item.Item.Path) ?? "")
        .Select(group => new DirectoryCandidates(group.Key, group.Select(item => {
          var inline = text[item.Id];
          return (object)new SkillCandidate(item.Id, System.IO.Path.GetFileName(item.Item.Path),
              item.Item.SizeBytes, item.Item.Name, inline.Text, inline.Reference);
        }).ToArray())).ToArray();
  }

  // Each optional property adds a comma, quoted name, colon and serialized value.
  // Text length is a lower bound on serialized bytes, so oversized bodies need no serialization.
  private static Dictionary<string, (string? Text, string? Reference)> AllocateText(
      IEnumerable<(string Id, string Text)> candidates, string textProperty, string referenceProperty, bool referenceOmitted) {
    var items = candidates.Select((item, index) => (item.Id, item.Text, Index: index,
        Bytes: item.Text.Length > MaxInlineTextBytes ? int.MaxValue : SerializedStringBytes(item.Text))).ToArray();
    var result = new Dictionary<string, (string? Text, string? Reference)>(StringComparer.Ordinal);
    var first = new Dictionary<string, string>(StringComparer.Ordinal);
    int remaining = InlineTextBudget;
    foreach (var item in items.OrderBy(item => item.Bytes).ThenBy(item => item.Index)) {
      if (first.TryGetValue(item.Text, out string? reference)) {
        int cost = referenceProperty.Length + 4 + SerializedStringBytes(reference);
        if (cost <= remaining) {
          result.Add(item.Id, (null, reference));
          remaining -= cost;
        } else {
          result.Add(item.Id, (null, null));
        }
      } else {
        int cost = item.Bytes == int.MaxValue ? int.MaxValue : textProperty.Length + 4 + item.Bytes;
        bool include = item.Bytes <= MaxInlineTextBytes && cost <= remaining;
        result.Add(item.Id, include ? (item.Text, null) : (null, null));
        if (include)
          remaining -= cost;
        if (include || referenceOmitted)
          first.Add(item.Text, item.Id);
      }
    }
    return result;
  }

  private static int SerializedStringBytes(string value) => Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(value));

  private sealed record DirectoryCandidates(string Directory, object[] Files);
  private sealed record InstructionCandidate(string Id, string File, long SizeBytes, string Scope,
      [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Content,
      [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ContentRef);
  private sealed record SkillCandidate(string Id, string File, long? SizeBytes, string Name,
      [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Description,
      [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DescriptionRef);
  private sealed record ToolCandidate(string Id, string Type, string Name, string? Description, JsonElement Schema);
}
