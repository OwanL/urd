using Urd.Core.Sessions;
using Urd.Core.Storage;
using Urd.Core.Tools;

namespace Urd.Core.Execution;

// Restricts harness tool session access to identity lookup, bounded child runs and the
// question broker, all owned by the invoking session's manager.
internal sealed class ToolSessionServices(SessionStorage sessions, ISubagentRunner? subagents = null,
    IAgentQuestions? questions = null) : IHarnessSessionServices {
  public SessionRecord? Read(Guid sessionId) => sessions.Read(sessionId);

  public Task<IReadOnlyList<LiveAgent>> ListLiveAgentsAsync(CancellationToken cancellation) =>
      subagents?.ListLiveAgentsAsync(cancellation)
          ?? throw new InvalidOperationException("This session does not support agent listing.");

  public Task<SubagentRunResult> RunSubagentAsync(HarnessToolContext caller, SubagentRunRequest request,
      CancellationToken cancellation) =>
      subagents?.RunSubagentAsync(caller, request, cancellation)
          ?? throw new InvalidOperationException("This session does not support subagent runs.");

  public Task<AgentQuestionResult> AskAgentAsync(HarnessToolContext caller, string recipient, string prompt,
      bool wake, CancellationToken cancellation) =>
      questions?.AskAgentAsync(caller, recipient, prompt, wake, cancellation)
          ?? throw new InvalidOperationException("This session does not support agent questions.");

  public bool AnswerAgent(HarnessToolContext caller, string answer) =>
      questions?.AnswerAgent(caller, answer)
          ?? throw new InvalidOperationException("This session does not support agent questions.");
}
