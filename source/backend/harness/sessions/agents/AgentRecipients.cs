using Urd.Core.Storage;
using Urd.Core.Tools;

namespace Urd.Core.Sessions;

// Resolves live recipients and routes questions without holding the registry across session work.
internal sealed class AgentRecipients(UrdStore store, AgentQuestions questions, SessionRegistry registry) {
  internal async Task<IReadOnlyList<LiveAgent>> ListLiveAgentsAsync(CancellationToken cancellation) {
    SessionBase[] handles = await registry.SnapshotAsync(cancellation);
    var observations = new List<(Guid Id, Guid? ParentId, string? Title, bool Generating, string State)>();
    foreach (SessionBase session in handles) {
      string? status = null;
      using IDisposable subscription = await session.SubscribeAsync(change => status ??= change.Status, watched: false, cancellation);
      SessionSnapshot snapshot;
      try {
        snapshot = await session.ReadSnapshotAsync(cancellation);
      } catch (KeyNotFoundException) when (session.IsClosed) {
        continue;
      }
      (string? title, bool generating) = store.Sessions.ReadAgentTitle(session.Id);
      if (store.Sessions.Read(session.Id) is not { } record)
        continue;
      string state = status switch {
        "waiting" => "waiting for background task",
        "running" or "compacting" => "active",
        _ => snapshot.UnsettledExecutionId is null ? "idle" : "active"
      };
      observations.Add((session.Id, record.ParentSessionId, title, generating, state));
    }

    HashSet<Guid> liveIds = await registry.LiveIdsAsync(cancellation);
    observations.RemoveAll(agent => !liveIds.Contains(agent.Id));
    IReadOnlyDictionary<Guid, AgentTitle> titles = AgentTitles.ForListing(
        observations.Select(agent => (agent.Id, agent.Title, agent.Generating)).ToArray());
    return observations.Select(agent => new LiveAgent(titles[agent.Id], agent.State,
        agent.ParentId is { } parent && titles.TryGetValue(parent, out AgentTitle? parentTitle) ? parentTitle : null)).ToArray();
  }

  internal async Task<AgentQuestionResult> AskAgentAsync(HarnessToolContext caller, string recipient,
      string prompt, bool wake, CancellationToken cancellation) {
    ArgumentNullException.ThrowIfNull(caller);
    ArgumentException.ThrowIfNullOrWhiteSpace(recipient);
    ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

    (Guid? recipientId, string? error) = recipient == ParentAlias
        ? await ResolveParentAsync(caller.SessionId, cancellation)
        : await ResolveTitleAsync(recipient, cancellation);
    if (recipientId is not { } pinned)
      return new(null, error);

    SessionBase? target = await registry.FindOpenAsync(pinned, cancellation);
    // A racing disposal or shutdown closes the mailbox after the handle is captured.
    return target is null ? new(null, FinishedMessage(recipient))
        : await target.Questions.QueueQuestionAsync(caller, prompt, wake, cancellation);
  }

  internal bool AnswerAgent(HarnessToolContext caller, string answer) {
    ArgumentNullException.ThrowIfNull(caller);
    ArgumentException.ThrowIfNullOrWhiteSpace(answer);
    return questions.AnswerPresented(caller.SessionId, caller.RequestId, answer);
  }

  private async Task<(Guid? RecipientId, string? Error)> ResolveTitleAsync(string recipient, CancellationToken cancellation) {
    SessionBase[] handles = await registry.SnapshotAsync(cancellation);
    var titled = new List<(Guid Id, string? Title, bool Generating)>();
    foreach (SessionBase session in handles) {
      (string? title, bool generating) = store.Sessions.ReadAgentTitle(session.Id);
      if (!session.IsClosed)
        titled.Add((session.Id, title, generating));
    }

    RecipientLookup lookup = AgentAddresses.Match(AgentTitles.ForListing(titled), recipient);
    if (lookup.SessionId is { } matched)
      return (matched, null);
    if (lookup.Ambiguous)
      return (null, $"The title '{recipient}' matches multiple live agents; address a different title.");
    if (lookup.PlaceholderMatch)
      return (null, PlaceholderMessage(recipient));

    IReadOnlyList<Guid> finished = store.Sessions.ReadSubagentIdsByTitle(recipient);
    return (null, finished.Count > 0 ? FinishedMessage(recipient) : UnknownMessage(recipient));
  }

  private async Task<(Guid? RecipientId, string? Error)> ResolveParentAsync(Guid callerId, CancellationToken cancellation) {
    SessionRecord? caller = store.Sessions.Read(callerId);
    if (caller?.ParentSessionId is not { } parentId)
      return (null, "The calling agent has no parent.");

    if (!await registry.IsLiveAsync(parentId, cancellation))
      return (null, "The parent agent has finished and cannot answer questions.");

    if (store.Sessions.ReadAgentTitle(parentId).Title is null)
      return (null, "The parent agent has no final title yet; wait a few seconds and try again.");
    return (parentId, null);
  }

  private const string ParentAlias = "parent";

  private static string PlaceholderMessage(string recipient) =>
      $"The agent '{recipient}' has no final title yet; wait a few seconds and try again.";

  private static string FinishedMessage(string recipient) =>
      $"The agent '{recipient}' has finished and cannot answer questions.";

  private static string UnknownMessage(string recipient) =>
      $"No live agent has the title '{recipient}'; use list_agents for current titles.";
}
