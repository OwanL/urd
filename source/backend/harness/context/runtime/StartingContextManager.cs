using System.Reflection;
using Microsoft.Extensions.AI;
using Urd.Core.Providers;
using Urd.Core.Tools;

namespace Urd.Core.Context;

// Composes the initial model instructions and available tools without loading external context.
internal sealed class StartingContextManager {
  private static readonly Type[] guidanceTypes = DiscoverGuidanceTypes();

  private readonly ToolCatalog toolCatalog;
  private readonly IReadOnlyList<ContextGuidance> guidance;

  internal StartingContextManager(ToolCatalog toolCatalog, IReadOnlyList<ContextGuidance>? additionalGuidance = null) {
    ArgumentNullException.ThrowIfNull(toolCatalog);
    this.toolCatalog = toolCatalog;
    guidance = Array.AsReadOnly(guidanceTypes
        .Select(CreateGuidance)
        .Concat(additionalGuidance ?? [])
        .OrderBy(item => item.Order)
        .ThenBy(item => item.GetType().FullName, StringComparer.Ordinal)
        .ToArray());
  }

  internal Task<StartingContext> CreateAsync(StartingContextInput input, CancellationToken cancellation) {
    if (cancellation.IsCancellationRequested)
      return Task.FromCanceled<StartingContext>(cancellation);

    if (input.SessionKind is not (SessionKind.Primary or SessionKind.Subagent))
      throw new ArgumentOutOfRangeException(nameof(input), input.SessionKind, "Unsupported session kind.");

    var instructions = new List<ChatMessage>();
    foreach (ContextGuidance item in guidance) {
      if (cancellation.IsCancellationRequested)
        return Task.FromCanceled<StartingContext>(cancellation);

      if (!item.AppliesTo(input))
        continue;

      string? text = item.GetText(input);
      if (!string.IsNullOrWhiteSpace(text))
        instructions.Add(new(ChatRole.System, text));
    }

    if (cancellation.IsCancellationRequested)
      return Task.FromCanceled<StartingContext>(cancellation);

    AIFunctionDeclaration[] tools = toolCatalog.For(input.ModelFamily)
        .Select(AgentToolDeclaration.Create)
        .ToArray();
    if (cancellation.IsCancellationRequested)
      return Task.FromCanceled<StartingContext>(cancellation);

    return Task.FromResult(new StartingContext(
        Array.AsReadOnly(instructions.ToArray()), Array.AsReadOnly(tools)));
  }

#pragma warning disable S3011 // Discover and construct only parameterless guidance in Urd Core, never external plugin types.
  private static Type[] DiscoverGuidanceTypes() =>
      typeof(ContextGuidance).Assembly.GetTypes()
          .Where(type =>
              type.IsClass &&
              !type.IsAbstract &&
              !type.ContainsGenericParameters &&
              type.IsSubclassOf(typeof(ContextGuidance)) &&
              type.GetConstructor(
                  BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                  binder: null,
                  Type.EmptyTypes,
                  modifiers: null) is not null)
          .OrderBy(type => type.FullName, StringComparer.Ordinal)
          .ToArray();

  private static ContextGuidance CreateGuidance(Type type) =>
      (ContextGuidance)Activator.CreateInstance(type, nonPublic: true)!;
#pragma warning restore S3011
}
