using System.Text.Json;

namespace Urd.Core.Tools;

// Common validation and delegation for family-facing file tool names.
internal abstract class FileToolVariant(AgentTool generic) : AgentTool {
  public override ToolRelevance RelevantWhen => generic.RelevantWhen;
  internal override bool RequiresWorkingDirectory => generic.RequiresWorkingDirectory;
  internal override bool SupportsBackgroundOption => generic.SupportsBackgroundOption;
  internal override bool ForcesBackground(JsonElement arguments) => generic.ForcesBackground(arguments);

  public override Task<ToolOutput> RunAsync(JsonElement arguments, ToolContext context, CancellationToken cancellation) {
    try {
      if (arguments.ValueKind != JsonValueKind.Object)
        throw new ArgumentException("Arguments must be an object.");
      JsonElement properties = ParametersSchema.GetProperty("properties");
      foreach (string name in arguments.EnumerateObject().Select(property => property.Name))
        if (!properties.TryGetProperty(name, out _))
          throw new ArgumentException($"Unsupported argument: {name}.");
      JsonElement translated = Translate(arguments);
      return RunTranslated(generic, translated, context, cancellation);
    } catch (Exception error) when (ToolFailures.IsExpected(error)) {
      return Task.FromResult(new ToolOutput($"{Name} failed: {error.Message}", [], IsError: true));
    }
  }

  protected abstract JsonElement Translate(JsonElement arguments);
  protected virtual Task<ToolOutput> RunTranslated(AgentTool tool, JsonElement arguments, ToolContext context,
      CancellationToken cancellation) => tool.RunAsync(arguments, context, cancellation);
}
