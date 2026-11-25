using Microsoft.Extensions.AI;
using Urd.Core.Context;
using Urd.Core.Storage;
using Urd.Core.Providers;
using Urd.Core.Preferences;
using Urd.Core.Tools;

namespace Urd.Core.Sessions;

// A bounded child session owned by one parent tool call. It runs a single assignment to a final
// report; the owning parent call admits its inputs, so user messages are never accepted here.
public sealed class SubagentSession : SessionBase {
  internal Guid ParentSessionId { get; }

  internal SubagentSession(SessionRecord record, UrdStore store, Func<ProviderConnection, IChatClient> createChatClient,
      ToolCatalog? tools = null, ChatProcesses? processes = null, PendingToolCalls? pending = null,
      PreferenceManager? preferences = null, ModelSelector? modelSelector = null, ISubagentRunner? subagents = null,
      int depth = 1)
      : base(record, SessionKind.Subagent, store, createChatClient, tools, processes, pending, preferences,
          modelSelector, subagents, depth) {
    if (record.Kind != SessionKind.Subagent || record.ParentSessionId is null || record.OwningCallId is null)
      throw new ArgumentException("Subagent records require a parent session and an owning call.", nameof(record));
    ParentSessionId = record.ParentSessionId.Value;
  }

  // The inherited context opens the assignment as quoted background data and the task follows as
  // its own input, so the title prepass sees only the task. One admission hold keeps both in the
  // child's first request.
  internal Task<Guid> StartAssignmentAsync(string task, string? contextBlock, LlmSelection selection) {
    ArgumentException.ThrowIfNullOrWhiteSpace(task);
    selection.Validate();
    UserMessageSubmission[] inputs;
    if (contextBlock is null)
      inputs = [new UserMessageSubmission(Guid.NewGuid(), 0, task, selection)];
    else
      inputs = [new UserMessageSubmission(Guid.NewGuid(), 0, contextBlock, selection),
                new UserMessageSubmission(Guid.NewGuid(), 0, task, selection)];
    return AdmitAssignmentAsync(inputs, ParentSessionId);
  }

  // A child starts from its parent's active selection and keeps its own after compaction.
  internal override StartingContext? ReadInheritedSelection() =>
      ReadStartingContext() ?? ReadActiveStartingContext(ParentSessionId);
}
