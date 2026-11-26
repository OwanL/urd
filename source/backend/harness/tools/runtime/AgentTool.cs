using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Urd.Core.Tools;

// One model-facing tool name and the implementation that interprets its raw arguments.
#pragma warning disable S1694 // Owner-approved tool base shape; not a public interface contract.
internal abstract class AgentTool {
  public abstract string Name { get; }
  public abstract ToolRelevance RelevantWhen { get; }
  public abstract string Description { get; }
  public abstract JsonElement ParametersSchema { get; }

  // Whether invocation needs the chat's saved working folder. Tools that do not use it can run
  // when no folder was configured.
  internal virtual bool RequiresWorkingDirectory => true;

  // Whether the tool's own arguments force a background invocation. This is decided before the
  // durable claim; an ask with wake false runs in the background because its answer may arrive
  // only opportunistically.
  internal virtual bool ForcesBackground(JsonElement arguments) => false;
  internal virtual bool SupportsBackgroundOption => true;

  public abstract Task<ToolOutput> RunAsync(
    JsonElement arguments, ToolContext context, CancellationToken cancellation);
}
#pragma warning restore S1694

// Invocation identity and chat defaults; the working directory is not a sandbox.
internal sealed record ToolContext(
  string WorkingDirectory, Guid RequestId, string CallId,
  bool ModelAcceptsImages, Action<ToolOutput> ReportProgress);

// A tool's text and optional images, with structured detail retained for consumers.
internal sealed record ToolOutput(
  string Text, IReadOnlyList<DataContent> Images,
  JsonElement? Details = null, bool IsError = false, ToolOutputSpill? Spill = null);

// Internal display boundaries for an already saved complete output, independent of tool details.
internal sealed record ToolOutputSpill(string Path, bool Tail = false, int? PreviewEnd = null, string Suffix = "");
