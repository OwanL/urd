namespace Urd.Core.Providers;

// The configured auxiliary purpose choice is unset or unresolvable, so callers may stand in their
// own selection; not a provider call failure after resolution.
public sealed class AuxiliaryModelUnavailableException(string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException);
