namespace Urd.Core.Sessions;

/// <summary>The original decoded input. Retries must preserve every member unchanged.</summary>
public sealed record InputCommand(Guid InputId, long ExpectedHistoryRevision, string Text);
