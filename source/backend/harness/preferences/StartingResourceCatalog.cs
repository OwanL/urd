namespace Urd.Core.Preferences;

public sealed record StartingResourceOption(string Id, string Name);

public sealed record StartingResourceCatalog(
    IReadOnlyList<StartingResourceOption> Tools,
    IReadOnlyList<StartingResourceOption> Skills,
    IReadOnlyList<StartingResourceOption> Instructions);
