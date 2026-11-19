using Urd.Core.Preferences;

namespace Urd.Core.Providers;

public sealed record BucketAssignment(ModelBucket Bucket, string ModelId);
public sealed record BucketSuggestions(ProviderConnection Connection, IReadOnlyList<BucketAssignment> Assignments);
