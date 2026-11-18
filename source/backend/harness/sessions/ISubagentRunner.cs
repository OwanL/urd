using Urd.Core.Tools;

namespace Urd.Core.Sessions;

// Owns bounded child runs: creating and registering children under the shutdown fence, running
// them to their final report and disposing them after settlement.
internal interface ISubagentRunner {
  Task<IReadOnlyList<LiveAgent>> ListLiveAgentsAsync(CancellationToken cancellation);

  Task<SubagentRunResult> RunSubagentAsync(HarnessToolContext caller, SubagentRunRequest request,
      CancellationToken cancellation);
}
