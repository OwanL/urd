using Urd.Core.Sessions;

namespace Urd.Core.MarkdownRendering;

// Independently disposable ownership, pinned before request publication. Cancellation
// only abandons unpublished interests; delivered leases belong to their consumer.
public sealed class MessageMarkdownLease : IDisposable {
  MessageMarkdownScheduler? owner;
  internal MessageMarkdownCache.Entry? Entry { get; private set; }
  internal MessageMarkdownKey Key { get; }
  public MarkdownRenderManifest Manifest { get; }
  public TranscriptTextFacts Source => Key.TextFacts;
  internal Guid Generation => Manifest.Generation;
  internal int SourceUtf8Bytes => Manifest.SourceUtf8Bytes;

  internal MessageMarkdownLease(MessageMarkdownScheduler owner, MessageMarkdownCache.Entry entry) {
    this.owner = owner;
    Entry = entry;
    Key = entry.Key;
    Manifest = entry.Index.Manifest;
  }

  internal MessageMarkdownCache.Entry Get(MessageMarkdownScheduler expectedOwner) {
    if (owner != expectedOwner || Entry is null) throw new MarkdownUnavailableException();
    return Entry;
  }

  // Called under the owner's gate; dropping both references releases the detached
  // index and service even if a disposed lease/task is retained by its caller.
  internal void Clear() {
    Entry = null;
    owner = null;
  }

  public MarkdownRenderCursor Locate(int sourceUtf8Offset) => GetOwner().Locate(this, sourceUtf8Offset);

  public MarkdownRenderCursor Locate(MessageContentCursor savedSourceCursor) =>
      GetOwner().Locate(this, savedSourceCursor);

  public MessageContentCursor SourceCursor(int globalUtf8Offset) => GetOwner().SourceCursor(this, globalUtf8Offset);

  public MarkdownPiecePage ReadPieces(MarkdownRenderCursor cursor) => GetOwner().Read(this, cursor);

  public MarkdownPiecePage ReadPiecesBefore(MarkdownRenderCursor exclusiveEndCursor) => GetOwner().ReadBefore(this, exclusiveEndCursor);

  public MarkdownContextDescriptorPage ReadContexts(IReadOnlyList<int> contextIds, MarkdownRenderCursor cursor) =>
      GetOwner().ReadContexts(this, contextIds, cursor);

  public MarkdownContextPage ReadContext(int contextId, MarkdownRenderCursor cursor) =>
      GetOwner().ReadContext(this, contextId, cursor);

  public void Dispose() => Volatile.Read(ref owner)?.Release(this);

  MessageMarkdownScheduler GetOwner() =>
      Volatile.Read(ref owner) ?? throw new MarkdownUnavailableException();
}
