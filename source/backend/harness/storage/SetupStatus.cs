namespace Urd.Core.Storage;

public sealed record SetupStatus(bool Completed, bool HasProvider, bool HasModelBuckets, bool HasWorkingDirectory);
