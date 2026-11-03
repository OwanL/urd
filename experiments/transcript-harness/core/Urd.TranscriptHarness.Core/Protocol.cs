// IPC protocol between the runner/oracle (parent) and the store host (child).
// One JSON line per request over the child's stdin; one JSON line per response
// on stdout; "ready" is the first event, "fatal" aborts the run. No other
// stdout traffic is permitted (that is why adapters must not log to stdout).

namespace Urd.TranscriptHarness.Core;

public static class Protocol
{
    public const string Ready = "ready";
    public const string Fatal = "fatal";
}

public static class ProtocolOps
{
    public const string Open = "open";
    public const string Describe = "describe";
    public const string ListSessions = "listSessions";
    public const string OpenRecent = "openRecent";
    public const string PageOlder = "pageOlder";
    public const string ReadRange = "readRange";
    public const string WindowPreview = "windowPreview";
    public const string FetchToolResult = "fetchToolResult";
    public const string Append = "append";
    public const string StreamDraft = "streamDraft";
    public const string Finalize = "finalize";
    public const string ReplaceSuffix = "replaceSuffix";
    public const string CreateAssignment = "createAssignment";
    public const string Settle = "settle";
    public const string GetAssignment = "getAssignment";
    public const string DeleteSession = "deleteSession";
    public const string Statistics = "statistics";
    public const string Close = "close";
}