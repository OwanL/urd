namespace Urd.Core.Sessions;

// Status is present for initial registration and status transitions; null marks a watched content change.
public sealed record SessionChange(Guid Session, long Version, string? Status = null);
