using System.Text;
using Urd.Core.MarkdownRendering;
using Urd.Core.Sessions;
using Xunit;

namespace Urd.Backend.Tests.MarkdownRendering;

public sealed class MessageMarkdownKeyTests {
  [Theory]
  [InlineData(0)]
  [InlineData(-1)]
  public void CacheLimitsRequirePositiveSmallMessageByteLimit(int smallMessageUtf8Bytes) {
    var limits = new MessageMarkdownCacheLimits(1, 1, 1, 1, smallMessageUtf8Bytes);

    Assert.Throws<ArgumentOutOfRangeException>(limits.Validate);
  }

  [Fact]
  public void MatchesRequiresBothReportedAndActualUtf8ByteCounts() {
    MessageMarkdownKey key = CreateKey(sourceUtf8Bytes: 2);
    MessageMarkdownSource source = CreateSource(key, "é");

    Assert.True(key.Matches(source));
    Assert.False(key.Matches(source with { Utf8Bytes = 1 }));
    Assert.False((key with { SourceUtf8Bytes = 1 }).Matches(source));
  }

  [Fact]
  public void CreatesKeysForUserAndAssistantPositionedEntries() {
    Guid sessionId = Guid.NewGuid();
    TranscriptPositionedEntry user = CreateEntry(user: true);
    TranscriptPositionedEntry assistant = CreateEntry(sourceUtf8Bytes: Encoding.UTF8.GetByteCount("a🙂"), final: true);

    MessageMarkdownKey userKey = MessageMarkdownKey.FromPositionedEntry(sessionId, user);
    MessageMarkdownKey assistantKey = MessageMarkdownKey.FromPositionedEntry(sessionId, assistant);

    Assert.Equal(sessionId, userKey.SessionId);
    Assert.Equal(user.Descriptor.Text!.From.SegmentIndex, userKey.FromSegment);
    Assert.Equal(user.Descriptor.Text.AvailableThrough.SegmentIndex, userKey.ThroughSegment);
    Assert.Equal(0, userKey.SourceUtf8Bytes);
    Assert.Equal(assistant.Descriptor.Entry.EntryId, assistantKey.Message.MessageId);
    Assert.Equal(Encoding.UTF8.GetByteCount("a🙂"), assistantKey.SourceUtf8Bytes);
    Assert.True(assistantKey.IsClosed);
    Assert.True(assistantKey.FinalOutputCommitted);
  }

  [Fact]
  public void RejectsMismatchedTranscriptIdentityAndInvalidTextFacts() {
    TranscriptPositionedEntry valid = CreateEntry();
    TranscriptEntryRef otherEntry = valid.Position.Entry with { EntryId = Guid.NewGuid() };
    AssertInvalid(valid with { Position = valid.Position with { Entry = otherEntry } });

    TranscriptTextFacts facts = valid.Descriptor.Text!;
    AssertInvalid(valid with {
      Descriptor = valid.Descriptor with {
        Text = facts with { From = facts.From with { Message = facts.From.Message with { MessageId = Guid.NewGuid() } } }
      }
    });
    MessageRef wrongRole = facts.From.Message with { Role = MessageRole.User };
    AssertInvalid(valid with {
      Descriptor = valid.Descriptor with {
        Text = facts with {
          From = facts.From with { Message = wrongRole },
          AvailableThrough = facts.AvailableThrough with { Message = wrongRole }
        }
      }
    });
    AssertInvalid(valid with { Descriptor = valid.Descriptor with { Text = null } });
    AssertInvalid(valid with {
      Descriptor = valid.Descriptor with { Text = facts with { IsClosed = false, FinalOutputCommitted = true } }
    });
    AssertInvalid(CreateEntry(kind: TranscriptEntryKind.Tool));
  }

  [Fact]
  public void RejectsInvalidSourceRangeOffsetsAndBounds() {
    AssertInvalid(CreateEntry(fromOffset: 1));
    AssertInvalid(CreateEntry(throughOffset: 1));
    AssertInvalid(CreateEntry(fromSegment: 2, throughSegment: 2));
    AssertInvalid(CreateEntry(fromSegment: -1, throughSegment: long.MaxValue));
    AssertInvalid(CreateEntry(fromSegment: long.MinValue, throughSegment: long.MaxValue));
    AssertInvalid(CreateEntry(fromSegment: 3, throughSegment: 2));
  }

  [Fact]
  public void RejectsUnsupportedByteCountsAndPositionArithmeticOverflow() {
    AssertInvalid(CreateEntry(rank: long.MaxValue));
    AssertInvalid(CreateEntry(prefixWeight: long.MaxValue));
    AssertInvalid(CreateEntry(weight: (long)int.MaxValue + 257));
  }

  static MessageMarkdownKey CreateKey(int sourceUtf8Bytes) => new(
      Guid.NewGuid(), new(Guid.NewGuid(), Guid.NewGuid(), MessageRole.User, 0), 0, 1, true, false, sourceUtf8Bytes);

  static MessageMarkdownSource CreateSource(MessageMarkdownKey key, string text) => new(
      key.SessionId, key.Message, new(key.Message, key.FromSegment, 0), new(key.Message, key.ThroughSegment, 0),
      Encoding.UTF8.GetByteCount(text), key.IsClosed, key.FinalOutputCommitted, text, []);

  static TranscriptPositionedEntry CreateEntry(bool user = false, int sourceUtf8Bytes = 0,
      bool closed = true, bool final = false, long rank = 0, long prefixWeight = 0,
      long? weight = null, long fromSegment = 0, long throughSegment = 1,
      int fromOffset = 0, int throughOffset = 0, TranscriptEntryKind? kind = null) {
    Guid executionId = Guid.NewGuid(), entryId = Guid.NewGuid();
    long revision = 3;
    TranscriptEntryKind entryKind = kind ?? (user ? TranscriptEntryKind.User : TranscriptEntryKind.Text);
    MessageRole role = user ? MessageRole.User : MessageRole.Assistant;
    var transcriptEntry = new TranscriptEntryRef(executionId, entryId, entryKind, revision);
    var message = new MessageRef(executionId, entryId, role, revision);
    var facts = new TranscriptTextFacts(
        new(message, fromSegment, fromOffset), new(message, throughSegment, throughOffset), closed, final);
    var descriptor = new TranscriptEntryDescriptor(transcriptEntry, null, facts, null);
    var position = new TranscriptPosition(transcriptEntry, rank, weight ?? 256L + sourceUtf8Bytes, prefixWeight);
    return new(descriptor, position);
  }

  static void AssertInvalid(TranscriptPositionedEntry entry) =>
      Assert.ThrowsAny<ArgumentException>(() => MessageMarkdownKey.FromPositionedEntry(Guid.NewGuid(), entry));
}
