namespace Urd.Core.Sessions;

// Matches one recipient name against a listing snapshot exactly as list_agents presents it,
// so addressing and discovery always agree on title text.
internal static class AgentAddresses {
  // Exactly one final title resolves; multiple finals are ambiguous and placeholders never do.
  internal static RecipientLookup Match(IReadOnlyDictionary<Guid, AgentTitle> titles, string recipient) {
    var matches = titles.Where(pair => string.Equals(pair.Value.Text, recipient, StringComparison.Ordinal))
        .Select(pair => (pair.Key, pair.Value)).ToArray();
    Guid[] finals = [.. matches.Where(match => !match.Value.IsPlaceholder).Select(match => match.Key)];
    return finals.Length switch {
      1 => new(finals[0], Ambiguous: false, PlaceholderMatch: false),
      > 1 => new(null, Ambiguous: true, PlaceholderMatch: false),
      _ => new(null, Ambiguous: false, PlaceholderMatch: matches.Any(match => match.Value.IsPlaceholder))
    };
  }
}

// One classified recipient name: a pinned live agent, an ambiguity, or a placeholder match.
internal sealed record RecipientLookup(Guid? SessionId, bool Ambiguous, bool PlaceholderMatch);
