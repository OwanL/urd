using System.Globalization;
using System.Reflection;
using Microsoft.Extensions.AI;
using Urd.Core.Preferences;
using Urd.Core.Providers;
using Urd.Core.Tools;

namespace Urd.Core.Context;

// Composes the initial model instructions and available tools without loading external context.
internal sealed class StartingContextBuilder {
  private static readonly Type[] guidanceTypes = DiscoverGuidanceTypes();
  private static readonly StringComparer pathComparer = OperatingSystem.IsWindows()
      ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

  private readonly ToolCatalog toolCatalog;
  private readonly IReadOnlyList<StartingContextContribution> guidance;
  private readonly InstructionResourceDiscovery? instructionDiscovery;

  internal StartingContextBuilder(ToolCatalog toolCatalog,
      IReadOnlyList<StartingContextContribution>? additionalGuidance = null,
      InstructionResourceDiscovery? instructionDiscovery = null) {
    ArgumentNullException.ThrowIfNull(toolCatalog);
    this.toolCatalog = toolCatalog;
    this.instructionDiscovery = instructionDiscovery;
    guidance = Array.AsReadOnly(guidanceTypes
        .Select(CreateGuidance)
        .Concat(additionalGuidance ?? [])
        .OrderBy(item => item.Order)
        .ThenBy(item => item.GetType().FullName, StringComparer.Ordinal)
        .ToArray());
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
        : await discovery.FindAsync(input.WorkingDirectory, cancellation);
    cancellation.ThrowIfCancellationRequested();

    HashSet<string> protectedToolNames = new(preferences.ProtectedResources.Tools, StringComparer.Ordinal);
    protectedToolNames.UnionWith(runtimeProtectedTools);
    AIFunctionDeclaration[] allTools = toolCatalog.Tools.Select(AgentToolDeclaration.Create).ToArray();
    string[] protectedSkillPaths = preferences.ProtectedResources.Skills.Select(CanonicalPath).ToArray();
    string[] protectedInstructionPaths = preferences.ProtectedResources.Instructions.Select(CanonicalPath).ToArray();
    var protectedSkills = new HashSet<string>(protectedSkillPaths, pathComparer);
    var protectedInstructions = new HashSet<string>(protectedInstructionPaths, pathComparer);

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
        .OrderBy(document => document.Order)
        .ThenBy(document => document.Path, StringComparer.Ordinal)
        .ToArray();

    StartingContextInput preparedInput = input with {
      SelectedSkills = Array.AsReadOnly(selectedSkills)
    };
    StartingContext context = Compose(preparedInput, selectedTools, cancellation, selectedInstructions);
    cancellation.ThrowIfCancellationRequested();
    return context with { BoundaryId = boundaryId };
  }

  private StartingContext Compose(
      StartingContextInput input,
      IReadOnlyList<AIFunctionDeclaration> tools,
      CancellationToken cancellation,
      IReadOnlyList<DiscoveredInstructionDocument>? documents = null) {
    cancellation.ThrowIfCancellationRequested();
    if (input.SessionKind is not (SessionKind.Primary or SessionKind.Subagent))
      throw new ArgumentOutOfRangeException(nameof(input), input.SessionKind, "Unsupported session kind.");

    var instructions = new List<ChatMessage>();
    foreach (StartingContextContribution item in guidance) {
      cancellation.ThrowIfCancellationRequested();
      if (!item.AppliesTo(input))
        continue;

      string? text = item.GetText(input);
      if (!string.IsNullOrWhiteSpace(text))
        instructions.Add(new(ChatRole.System, text));
    }

    foreach (DiscoveredInstructionDocument document in documents ?? []) {
      cancellation.ThrowIfCancellationRequested();
      string? text = document.GetText(input);
      if (!string.IsNullOrWhiteSpace(text))
        instructions.Add(new(ChatRole.System, text));
    }

    cancellation.ThrowIfCancellationRequested();
    return new(Array.AsReadOnly(instructions.ToArray()), Array.AsReadOnly(tools.ToArray()));
  }

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

#pragma warning disable S3011 // Discover and construct only parameterless guidance in Urd Core, never external plugin types.
  private static Type[] DiscoverGuidanceTypes() =>
      typeof(StartingContextContribution).Assembly.GetTypes()
          .Where(type =>
              type.IsClass &&
              !type.IsAbstract &&
              !type.ContainsGenericParameters &&
              type.IsSubclassOf(typeof(StartingContextContribution)) &&
              type.GetConstructor(
                  BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                  binder: null,
                  Type.EmptyTypes,
                  modifiers: null) is not null)
          .OrderBy(type => type.FullName, StringComparer.Ordinal)
          .ToArray();

  private static StartingContextContribution CreateGuidance(Type type) =>
      (StartingContextContribution)Activator.CreateInstance(type, nonPublic: true)!;
#pragma warning restore S3011
}
