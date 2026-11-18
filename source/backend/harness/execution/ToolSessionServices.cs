using Urd.Core.Sessions;
using Urd.Core.Storage;
using Urd.Core.Tools;

namespace Urd.Core.Execution;

// Restricts harness tool session access to identity lookup and bounded child runs.
internal sealed class ToolSessionServices(SessionStorage sessions, ISubagentRunner? subagents = null)
    : IHarnessSessionServices {
  public SessionRecord? Read(Guid sessionId) => sessions.Read(sessionId);

  public Task<SubagentRunResult> RunSubagentAsync(HarnessToolContext caller, SubagentRunRequest request,
      CancellationToken cancellation) =>
      subagents?.RunSubagentAsync(caller, request, cancellation)
          ?? throw new InvalidOperationException("This session does not support subagent runs.");
}
