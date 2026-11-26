using Microsoft.Playwright;

namespace Urd.Core.Tools;

// Saves downloads into the owner's temp folder and reports them as owner notices.
internal sealed partial class BrowserHost {
  private void OnDownload(Guid owner, IDownload download) {
    OwnerEntry? entry;
    lock (gate) {
      if (!owners.TryGetValue(owner, out entry))
        entry = null; // The owner is closed; cancel the download instead of saving it.
      else
        entry.Downloads++;
    }
    if (entry is null)
      _ = CancelQuietlyAsync(download);
    else
      _ = SaveDownloadAsync(owner, entry, download);
  }

  private async Task SaveDownloadAsync(Guid owner, OwnerEntry entry, IDownload download) {
    string name = download.SuggestedFilename;
    try {
      string target = UniqueTarget(entry.DownloadFolder, name);
      Directory.CreateDirectory(Path.GetDirectoryName(target)!);
      await download.SaveAsAsync(target);
      DownloadNotice(owner, entry, $"Saved download \"{name}\" to \"{target}\".");
    } catch (Exception error) {
      DownloadNotice(owner, entry, $"Download failed: {name} ({error.Message}).");
    } finally {
      bool cleanup;
      lock (gate)
        cleanup = --entry.Downloads == 0 && entry.Orphaned;
      if (cleanup)
        DeleteDownloadFolderQuietly(entry.DownloadFolder);
    }
  }

  // Each download gets its own directory, so concurrent saves cannot claim the same path.
  private static string UniqueTarget(string folder, string suggested) {
    string name = Path.GetFileName(suggested.Replace('\\', '/'));
    name = new string(name.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character).ToArray());
    if (string.IsNullOrWhiteSpace(name) || name is "." or "..")
      name = "download";
    return Path.Combine(folder, Guid.NewGuid().ToString("N"), name);
  }

  private static async Task CancelQuietlyAsync(IDownload download) {
    try {
      await download.CancelAsync().WaitAsync(CloseTimeout);
    } catch (Exception error) when (error is PlaywrightException or TimeoutException) {
      // The download may already be gone.
    }
  }

  private static void DeleteDownloadFolderQuietly(string folder) {
    try {
      Directory.Delete(folder, recursive: true);
    } catch (Exception error) when (error is IOException or UnauthorizedAccessException) {
      // A just-finished download may still hold the folder briefly.
    }
  }

  private void DownloadNotice(Guid owner, OwnerEntry expected, string text) {
    lock (gate) {
      if (owners.TryGetValue(owner, out OwnerEntry? entry) && entry == expected)
        entry.Notices.Add(text);
    }
  }
}
