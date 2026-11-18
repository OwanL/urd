using System.Text.Json;
using Urd.Core.Sessions;

namespace Urd.Core.Tools;

// A session-owned tool receives harness capabilities only through its dedicated context.
internal abstract class HarnessTool : AgentTool {
  public sealed override Task<ToolOutput> RunAsync(JsonElement arguments, ToolContext context, CancellationToken cancellation) =>
      throw new InvalidOperationException("Harness tools require a harness context.");

  public abstract Task<ToolOutput> RunAsync(JsonElement arguments, HarnessToolContext context, CancellationToken cancellation);
}

internal sealed record HarnessToolContext(Guid SessionId, Guid ExecutionId, string CallId, IHarnessSessionServices Sessions);

// Resolves durable session identities without exposing storage or session management to tools.
internal interface IHarnessSessionServices {
  SessionRecord? Read(Guid sessionId);
}
