using Urd.Core.Sessions;
using Urd.Core.Tools;
using Xunit;

namespace Urd.Backend.Tests.Sessions;

// Browser setup reports publish first-use provisioning notices.
public sealed class BrowserProvisioningTests {
  [Fact]
  public void TerminalNoticeWithoutSurfacedDownloadIsSuppressed() {
    var notices = new List<BrowserProvisioningNotice>();
    var progress = new BrowserSetupProgress(() => notices.Add);

    progress.Report(WebBrowserInstall.SetupFailed);
    progress.Report(WebBrowserInstall.DownloadFinished);

    Assert.Empty(notices);
  }

  [Fact]
  public void DownloadNoticePublishesAndTerminalNoticeClears() {
    var notices = new List<BrowserProvisioningNotice>();
    var progress = new BrowserSetupProgress(() => notices.Add);

    progress.Report(WebBrowserInstall.DownloadStarted);
    progress.Report(WebBrowserInstall.DownloadFinished);

    Assert.Equal(2, notices.Count);
    Assert.Equal(BrowserProvisioningStage.Downloading, notices[0].Stage);
    Assert.Equal(WebBrowserInstall.DownloadStarted, notices[0].Detail);
    Assert.Equal(BrowserProvisioningStage.Ready, notices[1].Stage);
    Assert.Null(notices[1].Detail);
  }

  [Fact]
  public void FailureNoticePublishesAfterDownloadAndIsClearedBeforeTheNextDownload() {
    var notices = new List<BrowserProvisioningNotice>();
    var progress = new BrowserSetupProgress(() => notices.Add);
    progress.Report(WebBrowserInstall.DownloadStarted);
    notices.Clear();

    progress.Report(WebBrowserInstall.SetupFailed);
    progress.Report(WebBrowserInstall.SetupFailed);
    Assert.Single(notices);
    Assert.Equal(BrowserProvisioningStage.Failed, notices[0].Stage);
    Assert.Null(notices[0].Detail);

    progress.Report(WebBrowserInstall.DownloadStarted);
    Assert.Equal(2, notices.Count);
    Assert.Equal(BrowserProvisioningStage.Downloading, notices[1].Stage);
  }

  [Fact]
  public void SubscriberFailureDoesNotPreventOtherSubscribers() {
    var notices = new List<BrowserProvisioningNotice>();
    Action<BrowserProvisioningNotice> subscribers = _ => throw new InvalidOperationException();
    subscribers += notices.Add;
    var progress = new BrowserSetupProgress(() => subscribers);

    progress.Report(WebBrowserInstall.DownloadStarted);

    Assert.Single(notices);
  }
}
