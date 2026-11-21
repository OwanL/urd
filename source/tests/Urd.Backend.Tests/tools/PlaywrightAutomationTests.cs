using Urd.Backend.Tests.Support;
using Urd.Core.Sessions;
using Urd.Core.Storage;
using Urd.Core.Tools;
using Xunit;

namespace Urd.Backend.Tests.Tools;

public sealed class PlaywrightAutomationTests {
  [Fact]
  public async Task LanPauseAndBrowserTaskAreMutuallyExclusive() {
    using var database = new TemporaryDatabase();
    using var store = UrdStore.Open(database.Path);
    await using var sessions = new SessionManager(store, _ => throw new InvalidOperationException("No model needed."));
    var browser = new PlaywrightAutomation(sessions, Path.GetDirectoryName(database.Path)!, () => 41000, []);
    var id = Guid.NewGuid();
    using (browser.PauseForLan()) {
      var denied = await browser.RunAsync(id, (_, _) => throw new InvalidOperationException("LAN must block browser operation."), CancellationToken.None);
      Assert.True(denied.IsError);
      Assert.Contains("LAN hosting", denied.Text);
    }

    try {
      var accepted = await browser.RunAsync(id, (_, _) => Task.FromResult(new ToolOutput("ok", [])), CancellationToken.None);
      Assert.False(accepted.IsError);
      Assert.Throws<InvalidOperationException>(() => browser.PauseForLan());
    } finally {
      await browser.ReleaseAsync(id);
    }
    using var pauseAgain = browser.PauseForLan();
  }
}
