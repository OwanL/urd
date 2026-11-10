using Urd.Core.Sessions;

namespace Urd.Core.Display;

// Access is serialized by MessageDisplays. Entries retain only detached records,
// owned strings and scalar source addresses, never captures, source text or ASTs.
internal sealed class MessageDisplayCache(MessageDisplayCacheLimits limits) {
  readonly Dictionary<MessageDisplayKey, LinkedListNode<Entry>> entries = [];
  readonly LinkedList<Entry> recent = [];
  internal int Count => entries.Count;
  internal long RetainedBytes { get; private set; }

  internal MessageDisplayReady? Find(MessageDisplayKey key) {
    if (!entries.TryGetValue(key, out var node)) return null;
    Touch(node);
    return node.Value.Ready;
  }

  internal MessageDisplayReady Add(MessageDisplayKey key, MessageDisplaySource source, MessageDisplayIndex index) {
    // Include the key, handle, LRU/dictionary bookkeeping, source cursor records,
    // segment array/references/records and pipeline string in addition to the index.
    // This is explicit conservative accounting, not a claim about exact CLR memory.
    long bytes = checked(index.AccountedBytes + 512 + 2L * key.Pipeline.Length + 24 + source.Segments.Count * 40L);
    if (bytes > limits.RetainedBytes) throw new DisplayTooLargeException();
    Remove(key);
    while (entries.Count >= limits.Entries || RetainedBytes > limits.RetainedBytes - bytes)
      Remove(recent.Last!.Value.Ready.Key);
    var ready = new MessageDisplayReady(key, index.Generation, index.SourceUtf8Bytes);
    var entry = new Entry(ready, index, source.Segments.ToArray(), bytes);
    entries.Add(key, recent.AddFirst(entry));
    RetainedBytes += bytes;
    return ready;
  }

  internal MessageDisplayIndex Get(MessageDisplayReady ready) {
    var node = GetEntry(ready);
    Touch(node);
    return node.Value.Index;
  }

  internal MessageContentCursor SourceCursor(MessageDisplayReady ready, int utf8Offset) {
    var node = GetEntry(ready);
    var entry = node.Value;
    if (utf8Offset < 0 || utf8Offset > entry.Index.SourceUtf8Bytes)
      throw new ArgumentOutOfRangeException(nameof(utf8Offset));
    Touch(node);
    if (utf8Offset == entry.Index.SourceUtf8Bytes)
      return new(ready.Key.Message, ready.Key.ThroughSegment, 0);
    int low = 0, high = entry.Segments.Length;
    while (low < high) {
      int middle = low + (high - low) / 2;
      if (entry.Segments[middle].Utf8Start <= utf8Offset) low = middle + 1;
      else high = middle;
    }
    var segment = entry.Segments[low - 1];
    return new(ready.Key.Message, segment.SegmentIndex, utf8Offset - segment.Utf8Start);
  }

  internal void Invalidate(Guid sessionId) {
    foreach (var key in entries.Keys.Where(key => key.SessionId == sessionId).ToArray()) Remove(key);
  }

  internal void Clear() {
    entries.Clear();
    recent.Clear();
    RetainedBytes = 0;
  }

  LinkedListNode<Entry> GetEntry(MessageDisplayReady ready) {
    if (!entries.TryGetValue(ready.Key, out var node) || node.Value.Index.Generation != ready.Generation)
      throw new DisplayUnavailableException();
    return node;
  }

  void Touch(LinkedListNode<Entry> node) {
    recent.Remove(node);
    recent.AddFirst(node);
  }

  void Remove(MessageDisplayKey key) {
    if (!entries.Remove(key, out var node)) return;
    recent.Remove(node);
    RetainedBytes -= node.Value.Bytes;
  }

  sealed record Entry(MessageDisplayReady Ready, MessageDisplayIndex Index, DisplaySourceSegment[] Segments, long Bytes);
}
