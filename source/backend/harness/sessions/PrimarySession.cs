using Microsoft.Extensions.AI;
using Urd.Core.Context;
using Urd.Core.Storage;
using Urd.Core.Providers;
using Urd.Core.Preferences;
using Urd.Core.Tools;

namespace Urd.Core.Sessions;

// The user-facing session: accepts typed user messages and sends them to the LLM.
public sealed partial class PrimarySession : SessionBase {
  internal PrimarySession(SessionRecord record, UrdStore store, Func<ProviderConnection, IChatClient> createChatClient,
      ToolCatalog? tools = null, ChatProcesses? processes = null, PendingToolCalls? pending = null,
      PreferenceManager? preferences = null, ModelSelector? modelSelector = null, ISubagentRunner? subagents = null)
      : base(record, SessionKind.Primary, store, createChatClient, tools, processes, pending, preferences,
          modelSelector, subagents) {
  }

  internal Task<(SessionCloseStatus Status, IReadOnlyList<Guid>? DeletedSessions)> CloseIfIdleAsync(Action ensureManagerOpen, CancellationToken cancellation) =>
      DeleteIfIdleAsync(ensureManagerOpen, cancellation);

  public Task<UserImage> SaveUserImageAsync(Guid imageId, byte[] data, CancellationToken cancellationToken = default) =>
      SaveImageAsync(imageId, data, cancellationToken);

  public Task<DataContent?> ReadUserImageAsync(Guid imageId, CancellationToken cancellationToken = default) =>
      ReadImageAsync(imageId, cancellationToken);

  // Returns a saved acceptance/refusal, without waiting for the LLM answer.
  public Task<UserMessageReceipt> SubmitUserMessageAsync(UserMessageSubmission submission) => AdmitUserMessageAsync(submission);

  public Task<ContinuationReceipt?> ContinueAsync(ContinuationSubmission submission) => AdmitContinuationAsync(submission);

  public Task<MessageEditReceipt> EditUserMessageAsync(MessageEditSubmission submission) => ReplaceUserMessageAsync(submission);
}
