using Urd.Core.Tools;

namespace Urd.Core.Sessions;

// Resolves addressees once and routes one exact answer per outstanding question. The manager
// owns this state; tools reach it only through IHarnessSessionServices.
internal interface IAgentQuestions {
  // Resolves the recipient from an exact final title or the parent alias, pins its internal
  // identity, rechecks liveness at enqueue and waits for the ask's single settlement.
  Task<AgentQuestionResult> AskAgentAsync(HarnessToolContext caller, string recipient, string prompt,
      bool wake, CancellationToken cancellation);

  // Completes the caller's current head question exactly once; false when nothing is queued.
  bool AnswerAgent(HarnessToolContext caller, string answer);
}
