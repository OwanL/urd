namespace Urd.Core.Sessions;

// Thrown when an input identity is reused with a different original command.
public sealed class InputConflictException(Guid inputId)
    : InvalidOperationException($"Input '{inputId}' already belongs to a different original command.") {
  public Guid InputId { get; } = inputId;
}
