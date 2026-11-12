using Microsoft.Extensions.AI;
using Urd.Core.Context;
using Urd.Core.Storage;
using Urd.Core.Providers;
using Urd.Core.Tools;

namespace Urd.Core.Sessions;

// The user-facing session: accepts typed user messages and sends them to the LLM.
public sealed class PrimarySession : SessionBase {
  internal PrimarySession(SessionRecord record, UrdStore store, Func<ProviderConnection, IChatClient> createChatClient,
      ToolCatalog? tools = null, ChatProcesses? processes = null, PendingToolCalls? pending = null)
      : base(record, SessionKind.Primary, store, createChatClient, tools, processes, pending) {
  }

  // Returns a saved acceptance/refusal, without waiting for the LLM answer.
  public Task<UserMessageReceipt> SubmitUserMessageAsync(UserMessageSubmission submission) => AdmitUserMessageAsync(submission);

  public Task<MessageEditReceipt> EditUserMessageAsync(MessageEditSubmission submission) => ReplaceUserMessageAsync(submission);
}
