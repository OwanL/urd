namespace Urd.Core.MarkdownRendering;

// Independently disposable ownership, pinned before request publication. Cancellation
// only abandons unpublished interests; delivered leases belong to their consumer.
internal sealed class MessageMarkdownLease : IDisposable {
  MessageMarkdownScheduler? owner;
  internal MessageMarkdownCache.Entry? Entry { get; private set; }
  internal MessageMarkdownKey Key { get; }
  internal Guid Generation { get; }
  internal int SourceUtf8Bytes { get; }

  internal MessageMarkdownLease(MessageMarkdownScheduler owner, MessageMarkdownCache.Entry entry) {
    this.owner = owner;
    Entry = entry;
    Key = entry.Key;
    Generation = entry.Index.Generation;
    SourceUtf8Bytes = entry.Index.SourceUtf8Bytes;
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

  public void Dispose() => Volatile.Read(ref owner)?.Release(this);
}
