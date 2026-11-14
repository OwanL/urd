namespace Urd.Core.Usage;

public sealed record UsageContext(Guid? SessionId, Guid? ExecutionId, string Purpose) {
  public const string PropertyName = "urd.usage_context";
  public const string DefaultPurpose = "model";
}
