using Urd.Core.Tools;

namespace Urd.Core.Sessions;

// Host-wide browser provisioning stages published while a first-use Chromium install is in flight.
public enum BrowserProvisioningStage { Downloading, Ready, Failed }

// Detail carries the installer's report while downloading; terminal stages have none.
public sealed record BrowserProvisioningNotice(BrowserProvisioningStage Stage, string? Detail);

// Translates browser install reports synchronously into ordered provisioning notices.
internal sealed class BrowserSetupProgress(Func<Action<BrowserProvisioningNotice>?> subscribers) : IProgress<string> {
  private bool browserProvisioningActive;

  public void Report(string report) {
    if (report == WebBrowserInstall.DownloadStarted)
      browserProvisioningActive = true;
    else if (!browserProvisioningActive)
      return;
    else if (report is WebBrowserInstall.DownloadFinished or WebBrowserInstall.SetupFailed)
      browserProvisioningActive = false;

    BrowserProvisioningStage stage = report switch {
      WebBrowserInstall.DownloadStarted => BrowserProvisioningStage.Downloading,
      WebBrowserInstall.DownloadFinished => BrowserProvisioningStage.Ready,
      _ => BrowserProvisioningStage.Failed
    };
    NotifyBrowserProvisioning(
        new BrowserProvisioningNotice(stage, stage == BrowserProvisioningStage.Downloading ? report : null));
  }

  private void NotifyBrowserProvisioning(BrowserProvisioningNotice notice) {
    Action<BrowserProvisioningNotice>? callbacks = subscribers();
    if (callbacks is null)
      return;

    foreach (Delegate callback in callbacks.GetInvocationList()) {
      try {
        ((Action<BrowserProvisioningNotice>)callback)(notice);
      } catch (Exception) {
        // Provisioning is observable only; subscriber failures are ignored.
      }
    }
  }
}
