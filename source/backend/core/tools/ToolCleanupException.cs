namespace Urd.Core.Tools;

// Owned work could not be joined safely; the execution must remain fenced rather than report a tool error.
#pragma warning disable S3871 // Internal driver signal, not a public exception contract.
internal sealed class ToolCleanupException(string message, Exception innerException) : Exception(message, innerException);
#pragma warning restore S3871
