using Microsoft.Playwright;

namespace Urd.Core.Tools;

// The vault shares the persistent profile, but is never registered as an agent page.
internal sealed partial class BrowserHost {
  internal Task<BrowserPage<IPage>> OpenBitwardenAsync(Guid owner, CancellationToken cancellation) {
    cancellation.ThrowIfCancellationRequested();
    Task<BrowserPage<IPage>> opening;
    lock (gate) {
      ObjectDisposedException.ThrowIf(disposed, this);
      if (!BitwardenEnabled)
        throw new InvalidOperationException("Bitwarden is unavailable.");
      if (!owners.TryGetValue(owner, out OwnerEntry? entry)) {
        entry = new OwnerEntry(Path.Combine(downloadRoot, $"{owner:N}-{Guid.NewGuid():N}"));
        owners.Add(owner, entry);
      }
      entry.LastActivity = DateTimeOffset.UtcNow;
      entry.DeferredUntil = null;
      if (entry.Bitwarden is { } existing && !existing.Page.IsClosed)
        return Task.FromResult(existing);
      if (entry.OpeningBitwarden is null) {
        entry.Active++;
        entry.OpeningBitwarden = OpenBitwardenCoreAsync(owner, entry);
        ObserveFailure(entry.OpeningBitwarden);
      }
      opening = entry.OpeningBitwarden;
    }
    return opening.WaitAsync(cancellation);
  }

  internal BrowserPage<IPage>? GetBitwarden(Guid owner, Guid page) {
    lock (gate) {
      if (owners.TryGetValue(owner, out OwnerEntry? entry) && entry.Bitwarden is { } vault &&
          vault.Id == page && !vault.Page.IsClosed)
        return vault;
      return null;
    }
  }

  internal async Task<bool> CloseBitwardenAsync(Guid owner, Guid page) {
    IPage? closing;
    lock (gate) {
      ObjectDisposedException.ThrowIf(disposed, this);
      if (!owners.TryGetValue(owner, out OwnerEntry? entry) || entry.Bitwarden is not { } vault || vault.Id != page)
        return false;
      entry.Bitwarden = null;
      entry.Pages--;
      entry.LastActivity = DateTimeOffset.UtcNow;
      entry.DeferredUntil = null;
      closing = vault.Page;
      UpdateEmpty();
    }
    NotifyChanged(owner);
    await CloseQuietlyAsync(closing);
    return true;
  }

  private async Task<BrowserPage<IPage>> OpenBitwardenCoreAsync(Guid owner, OwnerEntry entry) {
    await Task.Yield();
    IPage? opening = null;
    try {
      PersistentContext browser = await BeginLaunchAsync(shutdown.Token);
      string url = browser.BitwardenUrl ?? throw new InvalidOperationException("Bitwarden is unavailable.");
      opening = await browser.Context.NewPageAsync();
      IPage live = opening;
      var page = new BrowserPage<IPage>(Guid.NewGuid(), owner, live);
      // Vault popups must never enter the agent page registry.
      live.Popup += (sender, popup) => { _ = CloseQuietlyAsync(popup); };
      live.Close += (_, _) => OnBitwardenClosed(owner, entry, page);
      await BrowserInteraction.For(live).WatchNavigationAsync();
      await live.GotoAsync(url);
      lock (gate) {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!owners.TryGetValue(owner, out OwnerEntry? current) || current != entry || live.IsClosed)
          throw new InvalidOperationException("The chat or vault closed while the vault was opening.");
        entry.Bitwarden = page;
        entry.Pages++;
        entry.LastActivity = DateTimeOffset.UtcNow;
        entry.DeferredUntil = null;
        UpdateEmpty();
        Signal();
      }
      opening = null;
      NotifyChanged(owner);
      return page;
    } finally {
      if (opening is not null)
        await CloseQuietlyAsync(opening);
      lock (gate) {
        entry.Active--;
        entry.OpeningBitwarden = null;
        UpdateEmpty();
      }
    }
  }

  private void OnBitwardenClosed(Guid owner, OwnerEntry entry, BrowserPage<IPage> page) {
    bool removed = false;
    lock (gate) {
      if (owners.TryGetValue(owner, out OwnerEntry? current) && current == entry && entry.Bitwarden == page) {
        entry.Bitwarden = null;
        entry.Pages--;
        UpdateEmpty();
        removed = true;
      }
    }
    if (removed)
      NotifyChanged(owner);
  }
}
