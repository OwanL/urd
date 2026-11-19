namespace Urd.Core.Compaction;

// Local validation failures contain only fixed text, never model output.
#pragma warning disable S3871 // Internal signal translated at the execution boundary.
internal sealed class CompactionSummaryException(string message) : Exception(message);
#pragma warning restore S3871
