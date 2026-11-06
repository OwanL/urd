namespace Urd.Core.Sessions;

// Why submitted input was refused.
public enum InputRefusalReason
{
    StaleRevision = 1,
    Capacity = 2,
    Stopping = 3
}
