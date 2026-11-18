namespace Urd.Core.Sessions;

// Builds display titles together with their addressability, without changing stored titles.
internal static class AgentTitles {
  internal static IReadOnlyDictionary<Guid, AgentTitle> ForListing(
      IReadOnlyList<(Guid Id, string? Title, bool Generating)> sessions) {
    var result = new Dictionary<Guid, AgentTitle>();
    var used = sessions.Where(session => session.Title is not null)
        .Select(session => session.Title!).ToHashSet(StringComparer.Ordinal);
    foreach (var session in sessions) {
      if (session.Title is { } title) {
        result.Add(session.Id, new(title, IsPlaceholder: false));
        continue;
      }

      string basis = session.Generating ? "generating title" : "new session";
      string placeholder = basis;
      int suffix = 1;
      while (!used.Add(placeholder))
        placeholder = $"{basis} ({++suffix})";
      result.Add(session.Id, new(placeholder, IsPlaceholder: true));
    }
    return result;
  }
}
