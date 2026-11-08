using Microsoft.Extensions.AI;
using Urd.Core.Storage;

namespace Urd.Core.Sessions;

// The user-facing session: accepts typed user messages and sends them to the LLM.
public sealed class PrimarySession : SessionBase {
  internal PrimarySession(SessionRecord record, UrdStore store, IChatClient chatClient,
      IReadOnlyList<AIFunction>? tools = null) : base(record, store, chatClient, tools) {
  }

  // Returns a saved acceptance/refusal, without waiting for the LLM answer.
  public Task<UserMessageReceipt> SubmitUserMessageAsync(UserMessageSubmission submission) => AdmitUserMessageAsync(submission);
}
