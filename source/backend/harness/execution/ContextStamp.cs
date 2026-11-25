namespace Urd.Core.Execution;

// Claims what the model context of one request contains: the history revision it was created from,
// the effective compaction boundary, and how many tail omissions from continuations apply. Tool
// calls carry it on their harness context so a consumer can tell when a baseline it captured is no
// longer in the context the model will next see. Consumers fail closed when the stamp is missing.
internal sealed record ContextStamp(long HistoryRevision, Guid? CompactionId, int ContinuationOmissions);
