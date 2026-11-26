using Urd.Core.Tools;

namespace Urd.Core.Sessions;

// Asks the invoking session's user one focused question and waits for its single answer. Only
// the manager resolves the asking primary; tools reach it through this boundary.
internal interface IUserQuestions {
  Task<string> AskUserAsync(HarnessToolContext caller, string question, string? context,
      IReadOnlyList<string> answers, CancellationToken cancellation);
}
