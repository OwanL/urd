namespace Urd.Core.Context;

// The session's saved working directory, when one is available.
internal sealed class WorkingDirectoryGuidance : ContextGuidance {
  public override int Order => 200;

  public override bool AppliesTo(StartingContextInput input) =>
      input.WorkingDirectory is not null;

  public override string? GetText(StartingContextInput input) =>
      $"Working directory: {input.WorkingDirectory}";
}
