namespace Urd.Core.Sessions;

// Original decoded user input. Retries must repeat every member unchanged.
public sealed record InputCommand(Guid InputId, long ExpectedHistoryRevision, string Text);
