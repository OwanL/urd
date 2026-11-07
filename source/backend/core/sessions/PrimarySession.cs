using Microsoft.Extensions.AI;
using Urd.Core.Storage;

namespace Urd.Core.Sessions;

// The user-facing session: accepts typed user input and runs it against the model.
public sealed class PrimarySession : SessionBase {
  internal PrimarySession(SessionRecord record, SessionStore store, IChatClient chatClient,
      IReadOnlyList<AIFunction>? tools = null) : base(record, store, chatClient, tools) {
  }

  // Returns a saved acceptance/refusal decision, without waiting for the model answer.
  public Task<InputReceipt> SubmitUserInputAsync(InputCommand input) => AdmitInputAsync(input);
}
