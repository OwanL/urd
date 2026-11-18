using Urd.Backend.Tests.Support;
using Urd.Core.Sessions;
using Urd.Core.Storage;
using Xunit;

namespace Urd.Backend.Tests.Sessions;

// SessionManager owns the browser's first-use provisioning notices.
public sealed class BrowserProvisioningTests {
  [Fact]
  public void TerminalNoticeWithoutSurfacedDownloadIsSuppressed() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    var manager = new SessionManager(store, _ => new FakeChatClient());
    var notices = new List<BrowserProvisioningNotice>();
    manager.BrowserProvisioningChanged += notices.Add;

    manager.OnBrowserProgress(Urd.Core.Tools.WebBrowserInstall.SetupFailed);
    manager.OnBrowserProgress(Urd.Core.Tools.WebBrowserInstall.DownloadFinished);

    Assert.Empty(notices);
  }

  [Fact]
  public void DownloadNoticePublishesAndTerminalNoticeClears() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    var manager = new SessionManager(store, _ => new FakeChatClient());
    var notices = new List<BrowserProvisioningNotice>();
    manager.BrowserProvisioningChanged += notices.Add;

    manager.OnBrowserProgress(Urd.Core.Tools.WebBrowserInstall.DownloadStarted);
    manager.OnBrowserProgress(Urd.Core.Tools.WebBrowserInstall.DownloadFinished);

    Assert.Equal(2, notices.Count);
    Assert.Equal(BrowserProvisioningStage.Downloading, notices[0].Stage);
    Assert.Equal(Urd.Core.Tools.WebBrowserInstall.DownloadStarted, notices[0].Detail);
    Assert.Equal(BrowserProvisioningStage.Ready, notices[1].Stage);
    Assert.Null(notices[1].Detail);
  }

  [Fact]
  public void FailureNoticePublishesAfterDownloadAndIsClearedBeforeTheNextDownload() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    var manager = new SessionManager(store, _ => new FakeChatClient());
    var notices = new List<BrowserProvisioningNotice>();
    manager.BrowserProvisioningChanged += notices.Add;
    manager.OnBrowserProgress(Urd.Core.Tools.WebBrowserInstall.DownloadStarted);
    notices.Clear();

    manager.OnBrowserProgress(Urd.Core.Tools.WebBrowserInstall.SetupFailed);
    manager.OnBrowserProgress(Urd.Core.Tools.WebBrowserInstall.SetupFailed);
    Assert.Single(notices);
    Assert.Equal(BrowserProvisioningStage.Failed, notices[0].Stage);
    Assert.Null(notices[0].Detail);

    manager.OnBrowserProgress(Urd.Core.Tools.WebBrowserInstall.DownloadStarted);
    Assert.Equal(2, notices.Count);
    Assert.Equal(BrowserProvisioningStage.Downloading, notices[1].Stage);
  }
}
