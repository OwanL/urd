using Microsoft.Playwright;

namespace Urd.Core.Tools;

// Keeps the registry in step with what a live page does: navigation, popups and closing itself.
internal sealed partial class BrowserHost {
  private void Attach(Guid owner, Guid page, IPage live) {
    // Popups and downloads belong to the opener's owner. A page closed outside the host, by a
    // site or by the browser, removes itself so listings stay truthful.
    live.FrameNavigated += (_, frame) => {
      if (frame == live.MainFrame)
        NotifyChanged(owner);
    };
    live.Load += (_, _) => NotifyChanged(owner);
    live.Popup += (_, popup) => OnPopup(owner, page, popup);
    live.Download += (_, download) => OnDownload(owner, download);
    live.Close += (_, _) => OnPageSelfClosed(owner, page, live);
  }

  private void OnPopup(Guid owner, Guid opener, IPage popup) {
    _ = OnPopupCoreAsync(owner, opener, popup);
  }

  // A popup belongs to the opener's owner and becomes its most recent page. If the opener
  // was already removed when the popup arrives, close it; registered popups remain independent.
  private async Task OnPopupCoreAsync(Guid owner, Guid opener, IPage popup) {
    BrowserPage<IPage>? registered = null;
    BrowserPage<IPage>? evicted = null;
    lock (gate) {
      if (!disposed && owners.ContainsKey(owner) && pages.Get(owner, opener) is not null)
        registered = RegisterCore(owner, popup, out evicted, opener);
    }
    if (registered is null) {
      await CloseQuietlyAsync(popup);
      return;
    }
    BrowserInteraction interaction = BrowserInteraction.For(popup);
    Attach(owner, registered.Id, popup);
    try {
      await interaction.WatchNavigationAsync();
    } catch (PlaywrightException) {
      // A popup may close while the browser is attaching its DevTools session.
    }
    NotifyChanged(owner);
    if (evicted is not null)
      await CloseEvictedAsync(owner, evicted);
  }

  private void OnPageSelfClosed(Guid owner, Guid page, IPage live) {
    bool removed = false;
    lock (gate) {
      if (pages.Get(owner, page)?.Page == live) {
        pages.Remove(owner, page);
        removed = true;
        if (owners.TryGetValue(owner, out OwnerEntry? entry))
          entry.Pages--;
        UpdateEmpty();
      }
    }
    if (removed)
      NotifyChanged(owner);
  }
}
