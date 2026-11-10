using Urd.Core.Sessions;

namespace Urd.Core.MarkdownRendering;

// Serialized by MessageMarkdownScheduler. Detached generations stay accounted until both
// cache ownership and every lease are gone; stale generations are never cache hits.
internal sealed class MessageMarkdownCache(MessageMarkdownCacheLimits limits) {
  // Conservative allowance for each lease or queued task/interest/registration.
  internal const int LeaseBytes = 512;
  readonly Dictionary<MessageMarkdownKey, LinkedListNode<Entry>> entries = [];
  readonly LinkedList<Entry> recent = [];
  readonly HashSet<Entry> retained = [];
  int reservations;
  internal int Count { get; private set; }
  internal long RetainedBytes { get; private set; }

  internal void Reserve(Entry? protectedEntry = null) {
    if (reservations >= limits.Leases) throw new MarkdownCacheFullException();
    MakeRoom(0, LeaseBytes, protectedEntry);
    reservations++;
    RetainedBytes += LeaseBytes;
  }

  internal void ReleaseReservation() {
    reservations--;
    RetainedBytes -= LeaseBytes;
  }

  internal void ValidateSize(MessageMarkdownKey key) {
    foreach (var entry in retained) key.ValidateSize(entry.Key);
  }

  internal Entry? Find(MessageMarkdownKey key) {
    if (!entries.TryGetValue(key, out var node)) return null;
    Touch(node);
    return node.Value;
  }

  internal Entry Add(MessageMarkdownKey key, MessageMarkdownSource source, MarkdownRenderIndex index) {
    // Conservative accounting includes identity, LRU/dictionary and source mapping;
    // each independent lease/queued interest is accounted separately.
    long bytes = checked(index.AccountedBytes + 512 + 2L * key.Pipeline.Length + 24 + source.Segments.Count * 40L);
    if (bytes > limits.RetainedBytes - LeaseBytes) throw new MarkdownTooLargeException();
    MakeRoom(1, bytes);
    var entry = new Entry(key, index, source.Segments.ToArray(), bytes);
    entries.Add(key, recent.AddFirst(entry));
    retained.Add(entry);
    Count++;
    RetainedBytes += bytes;
    return entry;
  }

  internal void Release(Entry entry) {
    entry.Pins--;
    if (!entry.Cached && entry.Pins == 0) Forget(entry);
    ReleaseReservation();
  }

  internal MarkdownRenderIndex Get(Entry entry) {
    if (entry.Cached) Touch(entries[entry.Key]);
    return entry.Index;
  }

  internal MessageContentCursor SourceCursor(Entry entry, int utf8Offset) {
    var index = Get(entry);
    if (utf8Offset < 0 || utf8Offset > index.SourceUtf8Bytes)
      throw new ArgumentOutOfRangeException(nameof(utf8Offset));
    if (utf8Offset == index.SourceUtf8Bytes)
      return new(entry.Key.Message, entry.Key.ThroughSegment, 0);
    int low = 0, high = entry.Segments.Length;
    while (low < high) {
      int middle = low + (high - low) / 2;
      if (entry.Segments[middle].Utf8Start <= utf8Offset) low = middle + 1;
      else high = middle;
    }
    var segment = entry.Segments[low - 1];
    return new(entry.Key.Message, segment.SegmentIndex, utf8Offset - segment.Utf8Start);
  }

  internal void Invalidate(Guid sessionId) {
    foreach (var node in entries.Values.Where(node => node.Value.Key.SessionId == sessionId).ToArray()) Remove(node);
  }

  internal void Clear() {
    while (recent.Last is { } node) Remove(node);
  }

  void MakeRoom(int count, long bytes, Entry? protectedEntry = null) {
    if (Count <= limits.Entries - count && RetainedBytes <= limits.RetainedBytes - bytes) return;
    int removableCount = 0;
    long removableBytes = 0;
    foreach (var entry in recent.Where(entry => entry.Pins == 0 && entry != protectedEntry)) {
      removableCount++;
      removableBytes += entry.Bytes;
    }
    if (Count - removableCount > limits.Entries - count
        || RetainedBytes - removableBytes > limits.RetainedBytes - bytes)
      throw new MarkdownCacheFullException();
    var node = recent.Last;
    while (Count > limits.Entries - count || RetainedBytes > limits.RetainedBytes - bytes) {
      var previous = node!.Previous;
      if (node.Value.Pins == 0 && node.Value != protectedEntry) Remove(node);
      node = previous;
    }
  }

  void Touch(LinkedListNode<Entry> node) {
    recent.Remove(node);
    recent.AddFirst(node);
  }

  void Remove(LinkedListNode<Entry> node) {
    entries.Remove(node.Value.Key);
    recent.Remove(node);
    node.Value.Cached = false;
    if (node.Value.Pins == 0) Forget(node.Value);
  }

  void Forget(Entry entry) {
    retained.Remove(entry);
    Count--;
    RetainedBytes -= entry.Bytes;
  }

  internal sealed class Entry(MessageMarkdownKey key, MarkdownRenderIndex index, MarkdownSourceSegment[] segments, long bytes) {
    internal MessageMarkdownKey Key { get; } = key;
    internal MarkdownRenderIndex Index { get; } = index;
    internal MarkdownSourceSegment[] Segments { get; } = segments;
    internal long Bytes { get; } = bytes;
    internal int Pins { get; set; }
    internal bool Cached { get; set; } = true;
  }
}
