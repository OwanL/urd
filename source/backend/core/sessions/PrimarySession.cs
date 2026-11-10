using Microsoft.Extensions.AI;
using Urd.Core.Storage;
using Urd.Core.Providers;
using Urd.Core.Tools;

namespace Urd.Core.Sessions;

// The user-facing session: accepts typed user messages and sends them to the LLM.
public sealed class PrimarySession : SessionBase {
  internal PrimarySession(SessionRecord record, UrdStore store, Func<ProviderConnection, IChatClient> createChatClient,
      ToolCatalog? tools = null, ChatProcesses? processes = null) : base(record, store, createChatClient, tools, processes) {
  }

  // Returns a saved acceptance/refusal, without waiting for the LLM answer.
  public Task<UserMessageReceipt> SubmitUserMessageAsync(UserMessageSubmission submission) => AdmitUserMessageAsync(submission);
}
