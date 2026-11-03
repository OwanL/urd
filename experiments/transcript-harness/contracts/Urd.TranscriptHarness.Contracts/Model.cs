namespace Urd.TranscriptHarness.Contracts;

// Logical transcript model shared by every adapter. Content is what the oracle
// verifies; on-disk layout is each family's own choice and is never shared.

public abstract record MessagePart;

public sealed record TextPart(string Content) : MessagePart;

public sealed record ToolCallPart(string ToolCallId, string Name, string ArgsJson) : MessagePart;

// Tool-result content may be large; stores keep it inline or spill it however
// they like, but FetchToolResult must return it identically. ToolCallId pairs
// with a ToolCallPart in the same or an earlier message of the same session.
public sealed record ToolResultPart(string ToolCallId, string Content) : MessagePart;

// Images are files in the harness-owned attachment directory; persisted records
// carry only this stable reference plus relative path (agreed Urd direction).
public sealed record ImagePart(string ImageId, string RelativePath) : MessagePart;

public sealed record MessageRecord(string Role, IReadOnlyList<MessagePart> Parts)
{
    public static MessageRecord Single(string role, MessagePart part) => new(role, [part]);
}

public sealed record SessionMeta(string SessionId, long MessageCount);

public enum AssignmentStatus { Open, Settled, Cancelled }

public sealed record AssignmentRecord(
    string AssignmentId,
    string TargetSessionId,
    string Title,
    AssignmentStatus Status,
    string? ResultText);

public sealed record SettleAssignmentRequest(string AssignmentId, string ResultText);