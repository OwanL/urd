using Urd.Core.Tools;

namespace Urd.Core.Sessions;

// Builds each session's tool catalog from shared tool instances: file tools, one process owner
// for working-directory sessions and the session-, call- and browser-scoped harness tools.
// Catalog construction starts no processes; each session owns the process owner it receives.
internal sealed class SessionToolCatalog(AgentToolSettings? toolSettings, GitBash? bash, FileMutationQueue files,
    WebBrowser browser, Func<BrowserHost?> browserHost, IReadOnlyList<AgentTool> injectedTools,
    IUserQuestions userQuestions) {

  // Only sessions with configured agent tools capture a saved working directory.
  internal bool ProvidesWorkingDirectory => toolSettings is not null || injectedTools.Count > 0;

  internal ToolCatalog Create(PendingToolCalls pending, bool processTools, out ChatProcesses? processes) {
    processes = null;
    AgentTool[] shared = [new WebSearchTool(browser), new WebReadTool(browser),
      new ListPendingCallsTool(pending), new CancelPendingCallsTool(pending), new SubagentTool(), new ListAgentsTool(),
      new AskAgentTool(), new AnswerAgentTool(), new AskUserTool(userQuestions)];
    if (browserHost() is { } host)
      shared = [.. shared, new BrowserTool(host)];
    if (toolSettings is null || !processTools)
      return new([.. injectedTools, .. shared]);
    if (!OperatingSystem.IsWindows())
      throw new PlatformNotSupportedException("Agent tools currently require Windows and Git for Windows.");
    processes = new ChatProcesses(bash!);
    return new([
      new ReadTool(files), new WriteTool(files), new EditTool(files),
      new BashTool(processes, bash!, toolSettings.Environment), .. shared
    ]);
  }
}
