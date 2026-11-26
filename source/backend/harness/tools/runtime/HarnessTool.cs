using System.Text.Json;
using Urd.Core.Sessions;

namespace Urd.Core.Tools;

// A session-owned tool receives harness capabilities only through its dedicated context.
internal abstract class HarnessTool : AgentTool {
  internal sealed override bool RequiresWorkingDirectory => false;

  public sealed override Task<ToolOutput> RunAsync(JsonElement arguments, ToolContext context, CancellationToken cancellation) =>
      throw new InvalidOperationException("Harness tools require a harness context.");

  public abstract Task<ToolOutput> RunAsync(JsonElement arguments, HarnessToolContext context, CancellationToken cancellation);
}

internal sealed record HarnessToolContext(Guid SessionId, Guid ExecutionId, string CallId, IHarnessSessionServices Sessions) {
  internal Guid RequestId { get; init; }

  // The effective background mode decided before the durable claim, including the tool's own
  // argument rules such as an ask with wake false.
  internal bool Background { get; init; }

  // The invoking request's ContextStamp, so consumers can tell when a baseline they captured has
  // left the model context. Null when the executing pipeline could not stamp the call; consumers
  // must then fail closed.
  internal object? BrowserContextStamp { get; init; }
}

// Resolves durable session identities and bounded child runs without exposing storage or
// session management to tools.
internal interface IHarnessSessionServices {
  SessionRecord? Read(Guid sessionId);
  Task<IReadOnlyList<LiveAgent>> ListLiveAgentsAsync(CancellationToken cancellation);

  // Runs one child to its final report under the invoking session model. The caller context
  // owns the invoking request and call identity.
  Task<SubagentRunResult> RunSubagentAsync(HarnessToolContext caller, SubagentRunRequest request,
      CancellationToken cancellation);

  // Asks one live agent a question and waits for its single answer or stated failure.
  Task<AgentQuestionResult> AskAgentAsync(HarnessToolContext caller, string recipient, string prompt,
      bool wake, CancellationToken cancellation);

  // Completes the caller's current question exactly once; false when nothing is queued.
  bool AnswerAgent(HarnessToolContext caller, string answer);
}
