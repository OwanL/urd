using Urd.Core.Context;

namespace Urd.Core.Tools;

// One bounded delegation through a resolved bucket model. The parent inherits none of the child's
// work; context controls which parent context the child starts with.
internal sealed record SubagentRunRequest(string Bucket, SubagentContextMode Context, string Task);

// The child's final report, or the reason the bounded run produced none. A result without a
// report is final: the child was stopped, failed, or answered without usable output.
internal sealed record SubagentRunResult(Guid ChildSessionId, string? Report, string? Error);
