namespace Urd.Core.Tools;

// Invocation identity and chat defaults; the working directory is not a sandbox.
internal sealed record ToolContext(
  string WorkingDirectory, Guid RequestId, string CallId,
  bool ModelAcceptsImages, Action<ToolOutput> ReportProgress);
