namespace Urd.Core.Tools;

// All operations are atomic. Returned entries and lists are snapshots; callers own page lifetime.
internal sealed class BrowserPages<TPage> where TPage : class {
  private const int Limit = 3;
  private readonly Lock gate = new();
  private readonly Dictionary<Guid, OwnerPages> owners = [];
  private readonly Dictionary<Guid, PageEntry> pages = [];

  // A registered page becomes the owner's selection. An opener must belong to that owner
  // and is protected from eviction. The caller is responsible for closing an evicted page.
  public BrowserPageRegistration<TPage> Register(Guid ownerId, TPage page, Guid? openerId = null) {
    ArgumentNullException.ThrowIfNull(page);
    lock (gate) {
      if (openerId is Guid opener && (!pages.TryGetValue(opener, out var existing) || existing.OwnerId != ownerId))
        throw new ArgumentException("The opener must belong to the owner.", nameof(openerId));

      if (!owners.TryGetValue(ownerId, out var owner)) {
        owner = new OwnerPages();
        owners.Add(ownerId, owner);
      }

      Guid id;
      do id = Guid.NewGuid(); while (pages.ContainsKey(id));
      var entry = new PageEntry(id, ownerId, page);
      entry.Node = owner.Recent.AddLast(entry);
      pages.Add(id, entry);
      owner.SelectedId = id;

      BrowserPage<TPage>? evicted = null;
      if (owner.Recent.Count > Limit) {
        var victim = owner.Recent.First!;
        if (victim.Value.Id == openerId)
          victim = victim.Next!;
        evicted = Snapshot(victim.Value);
        owner.Recent.Remove(victim);
        pages.Remove(victim.Value.Id);
      }
      return new(Snapshot(entry), evicted);
    }
  }

  // Lists pages from least to most recently used, without changing recency.
  public IReadOnlyList<BrowserPage<TPage>> List(Guid ownerId) {
    lock (gate)
      return owners.TryGetValue(ownerId, out var owner) ? owner.Recent.Select(Snapshot).ToArray() : [];
  }

  public BrowserPage<TPage>? Get(Guid ownerId, Guid pageId) {
    lock (gate)
      return Find(ownerId, pageId) is { } entry ? Snapshot(entry) : null;
  }

  public BrowserPage<TPage>? Selected(Guid ownerId) {
    lock (gate)
      return owners.TryGetValue(ownerId, out var owner) && owner.SelectedId is Guid id
        ? Snapshot(pages[id]) : null;
  }

  public bool Select(Guid ownerId, Guid pageId) {
    lock (gate) {
      if (Find(ownerId, pageId) is not { } entry)
        return false;
      var owner = owners[ownerId];
      owner.SelectedId = pageId;
      Touch(owner, entry);
      return true;
    }
  }

  // Viewer interaction affects eviction but not agent selection.
  public bool Touch(Guid ownerId, Guid pageId) {
    lock (gate) {
      if (Find(ownerId, pageId) is not { } entry)
        return false;
      Touch(owners[ownerId], entry);
      return true;
    }
  }

  public BrowserPage<TPage>? Remove(Guid ownerId, Guid pageId) {
    lock (gate) {
      if (Find(ownerId, pageId) is not { } entry)
        return null;
      var owner = owners[ownerId];
      owner.Recent.Remove(entry.Node!);
      pages.Remove(pageId);
      if (owner.SelectedId == pageId)
        owner.SelectedId = owner.Recent.Last?.Value.Id;
      if (owner.Recent.Count == 0)
        owners.Remove(ownerId);
      return Snapshot(entry);
    }
  }

  private PageEntry? Find(Guid ownerId, Guid pageId) =>
    pages.TryGetValue(pageId, out var entry) && entry.OwnerId == ownerId ? entry : null;

  private static void Touch(OwnerPages owner, PageEntry entry) {
    owner.Recent.Remove(entry.Node!);
    owner.Recent.AddLast(entry.Node!);
  }

  private static BrowserPage<TPage> Snapshot(PageEntry entry) => new(entry.Id, entry.OwnerId, entry.Page);

  private sealed class OwnerPages {
    public LinkedList<PageEntry> Recent { get; } = [];
    public Guid? SelectedId { get; set; }
  }

  private sealed class PageEntry(Guid id, Guid ownerId, TPage page) {
    public Guid Id { get; } = id;
    public Guid OwnerId { get; } = ownerId;
    public TPage Page { get; } = page;
    public LinkedListNode<PageEntry>? Node { get; set; }
  }
}

internal sealed record BrowserPage<TPage>(Guid Id, Guid OwnerId, TPage Page);
internal sealed record BrowserPageRegistration<TPage>(BrowserPage<TPage> Page, BrowserPage<TPage>? Evicted);
