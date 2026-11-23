using System.Globalization;
using Microsoft.Extensions.AI;
using Urd.Core.Preferences;
using Urd.Core.Providers;
using Urd.Core.Tools;

namespace Urd.Core.Context;

// Composes agent guidance, session context, and available tools.
internal sealed class StartingContextBuilder {
  private static readonly StringComparer pathComparer = OperatingSystem.IsWindows()
      ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

  private readonly ToolCatalog toolCatalog;
  private readonly InstructionResourceDiscovery? instructionDiscovery;
  private readonly TimeProvider timeProvider;

  internal StartingContextBuilder(ToolCatalog toolCatalog, InstructionResourceDiscovery? instructionDiscovery = null, TimeProvider? timeProvider = null) {
    this.toolCatalog = toolCatalog ?? throw new ArgumentNullException(nameof(toolCatalog));
    this.instructionDiscovery = instructionDiscovery;
    this.timeProvider = timeProvider ?? TimeProvider.System;
  }

  internal StartingContext Create(StartingContextInput input, CancellationToken cancellation) {
    ArgumentNullException.ThrowIfNull(input);
    cancellation.ThrowIfCancellationRequested();
    AIFunctionDeclaration[] tools = toolCatalog.Tools
        .Select(AgentToolDeclaration.Create)
        .ToArray();
    return Compose(input, tools, cancellation);
  }

  internal async Task<StartingContext> PrepareAsync(
      StartingContextInput input,
      Guid boundaryId,
      string taskInput,
      UserPreferences preferences,
      IReadOnlySet<string> runtimeProtectedTools,
      StartingResourcePruner pruner,
      CancellationToken cancellation) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(taskInput);
    ArgumentNullException.ThrowIfNull(preferences);
    ArgumentNullException.ThrowIfNull(runtimeProtectedTools);
    ArgumentNullException.ThrowIfNull(pruner);
    cancellation.ThrowIfCancellationRequested();
    InstructionResourceDiscovery discovery = instructionDiscovery
        ?? throw new InvalidOperationException("Starting context preparation requires instruction resource discovery.");

    DiscoveredInstructionResources resources = input.WorkingDirectory is null
        ? new(Array.Empty<DiscoveredSkill>(), Array.Empty<DiscoveredInstructionDocument>())
        : await discovery.FindAsync(input.WorkingDirectory, preferences.SourceIgnores, cancellation);
    cancellation.ThrowIfCancellationRequested();

    HashSet<string> protectedToolNames = new(preferences.ProtectedResources.Tools, StringComparer.Ordinal);
    protectedToolNames.UnionWith(runtimeProtectedTools);
    AIFunctionDeclaration[] allTools = toolCatalog.Tools.Select(AgentToolDeclaration.Create).ToArray();
    var protectedSkills = ProtectedPaths(preferences.ProtectedResources.Skills);
    var protectedInstructions = ProtectedPaths(preferences.ProtectedResources.Instructions);

    AIFunctionDeclaration[] toolCandidates = allTools
        .Where(tool => !protectedToolNames.Contains(tool.Name))
        .ToArray();
    DiscoveredSkill[] skillCandidates = resources.Skills
        .Where(skill => !protectedSkills.Contains(CanonicalPath(skill.Identity)))
        .ToArray();
    DiscoveredInstructionDocument[] instructionCandidates = resources.Instructions
        .Where(document => !protectedInstructions.Contains(CanonicalPath(document.Identity)))
        .ToArray();

    string? indexWarning = IndexWarning(input.WorkingDirectory, resources.Index);

    int protectedToolCount = allTools.Length - toolCandidates.Length;
    int protectedSkillCount = resources.Skills.Count - skillCandidates.Length;
    int protectedInstructionCount = resources.Instructions.Count - instructionCandidates.Length;

    // Start every independent category before awaiting any result. Task.WhenAll does not
    // complete until all three operations have settled, including fatal and cancelled work.
    Task<StartingResourcePruningResult<AIFunctionDeclaration>> toolPruning =
        pruner.PruneAsync(toolCandidates, protectedToolCount, cancellation);
    Task<StartingResourcePruningResult<DiscoveredSkill>> skillPruning =
        pruner.PruneAsync(skillCandidates, protectedSkillCount, cancellation, indexWarning);
    Task<StartingResourcePruningResult<DiscoveredInstructionDocument>> instructionPruning =
        pruner.PruneAsync(instructionCandidates, protectedInstructionCount, cancellation, indexWarning);
    await Task.WhenAll(toolPruning, skillPruning, instructionPruning);

    cancellation.ThrowIfCancellationRequested();
    HashSet<string> retainedToolNames = toolPruning.Result.Retained
        .Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal);
    HashSet<string> retainedSkillPaths = skillPruning.Result.Retained
        .Select(skill => CanonicalPath(skill.Identity)).ToHashSet(pathComparer);
    HashSet<string> retainedInstructionPaths = instructionPruning.Result.Retained
        .Select(document => CanonicalPath(document.Identity)).ToHashSet(pathComparer);

    AIFunctionDeclaration[] selectedTools = allTools
        .Where(tool => protectedToolNames.Contains(tool.Name) || retainedToolNames.Contains(tool.Name))
        .ToArray();
    DiscoveredSkill[] retainedSkills = resources.Skills
        .Where(skill => protectedSkills.Contains(CanonicalPath(skill.Identity))
            || retainedSkillPaths.Contains(CanonicalPath(skill.Identity)))
        .ToArray();
    HashSet<string> selectedProjectNames = retainedSkills
        .Where(skill => !discovery.IsPackagedSkill(skill))
        .Select(skill => skill.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
    DiscoveredSkill[] selectedSkills = retainedSkills
        .Where(skill => !discovery.IsPackagedSkill(skill)
            || protectedSkills.Contains(CanonicalPath(skill.Identity))
            || !selectedProjectNames.Contains(skill.Name))
        .ToArray();
    DiscoveredInstructionDocument[] selectedInstructions = resources.Instructions
        .Where(document => protectedInstructions.Contains(CanonicalPath(document.Identity))
            || retainedInstructionPaths.Contains(CanonicalPath(document.Identity)))
        .OrderBy(document => document.ScopeDepth)
        .ThenBy(document => document.Path, StringComparer.Ordinal)
        .ToArray();

    StartingContext context = Compose(input, selectedTools, cancellation, selectedSkills, selectedInstructions);
    cancellation.ThrowIfCancellationRequested();
    return context with { BoundaryId = boundaryId };
  }

  private StartingContext Compose(
      StartingContextInput input,
      IReadOnlyList<AIFunctionDeclaration> tools,
      CancellationToken cancellation,
      IReadOnlyList<DiscoveredSkill>? skills = null,
      IReadOnlyList<DiscoveredInstructionDocument>? documents = null) {
    cancellation.ThrowIfCancellationRequested();
    string sessionGuidance = input.SessionKind switch {
      SessionKind.Primary => PrimarySessionGuidance.Text,
      SessionKind.Subagent => SubagentSessionGuidance.Text,
      _ => throw new ArgumentOutOfRangeException(nameof(input), input.SessionKind, "Unsupported session kind.")
    };
    var instructions = new List<ChatMessage> {
      new(ChatRole.System, SharedAgentGuidance.Text),
      new(ChatRole.System, sessionGuidance)
    };
    instructions.Add(new(ChatRole.System, SystemEnvironment.Describe(input.WorkingDirectory, timeProvider.GetUtcNow(), timeProvider.LocalTimeZone)));
    if (skills is not null && SelectedSkillsContext.Compose(skills) is { } selectedSkills)
      instructions.Add(new(ChatRole.System, selectedSkills));
    foreach (DiscoveredInstructionDocument document in documents ?? []) {
      cancellation.ThrowIfCancellationRequested();
      instructions.Add(new(ChatRole.System, document.GetText()));
    }

    cancellation.ThrowIfCancellationRequested();
    return new(Array.AsReadOnly(instructions.ToArray()), Array.AsReadOnly(tools.ToArray()));
  }

  internal static HashSet<string> ProtectedPaths(IEnumerable<string> paths) => new(paths.Select(CanonicalPath), pathComparer);

  private static string CanonicalPath(string path) => Path.GetFullPath(path);

  // A non-complete index serves paths that may be missing or stale; the pruning statuses say so plainly.
  private static string? IndexWarning(string? workingDirectory, InstructionResourceIndexState index) {
    if (workingDirectory is null)
      return null;
    return index.Phase switch {
      InstructionResourceIndexPhase.Refreshing =>
          $"Index of {workingDirectory} is being refreshed; results may be stale.",
      InstructionResourceIndexPhase.Partial =>
          $"Index of {workingDirectory} incomplete: {index.MatchedFiles.ToString("N0", CultureInfo.InvariantCulture)} files found so far.",
      _ => null,
    };
  }
}
