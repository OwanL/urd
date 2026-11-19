namespace Urd.Core.Providers;

// No eligible model resolved for the auxiliary purpose; not a provider call failure.
public sealed class AuxiliaryModelUnavailableException(string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException);
