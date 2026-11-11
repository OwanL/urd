namespace Urd.Core.Context;

// Instructions for sessions working directly with the user.
internal sealed class PrimaryGuidance : ContextGuidance {
  public override int Order => 100;

  public override bool AppliesTo(StartingContextInput input) =>
      input.SessionKind == SessionKind.Primary;

  public override string? GetText(StartingContextInput input) =>
      "Help the user directly and keep their request and goals central.";
}
