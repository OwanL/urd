namespace Urd.Core.Context;

// Shared identity and behavior for every session.
internal sealed class BaseGuidance : StartingContextContribution {
  public override int Order => 0;

  public override string? GetText(StartingContextInput input) => """
      You are Urd, a general-purpose assistant. Help with the user's request, be accurate, and make uncertainty clear.
      If you're only waiting on background tools, end your reply. Urd resumes you when a result is ready.
      """;
}
