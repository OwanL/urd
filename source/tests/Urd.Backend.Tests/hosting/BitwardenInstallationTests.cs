using Urd.Backend.Tests.Support;
using Urd.Server;
using Xunit;

namespace Urd.Backend.Tests.Hosting;

public sealed class BitwardenInstallationTests {
  [Fact]
  public async Task FreshProfileDefaultsOnAndFailedInstallDoesNotOptOut() {
    using var database = new TemporaryDatabase();
    string root = Path.GetDirectoryName(database.Path)!;
    using var installation = new BitwardenInstallation(root);
    Assert.Equal((true, false, false), installation.Status());

    // An incomplete installation fails locally, without contacting GitHub.
    string extension = Path.Combine(root, "bitwarden", "extension");
    Directory.CreateDirectory(extension);
    await installation.InitializeAsync(CancellationToken.None);
    Assert.Equal((true, false, false), installation.Status());
    Assert.True(Directory.Exists(extension));

    using var next = new BitwardenInstallation(root);
    Assert.Equal((true, false, false), next.Status());
    await next.ChangeAsync(false, CancellationToken.None);
    using var disabled = new BitwardenInstallation(root);
    await disabled.InitializeAsync(CancellationToken.None);
    Assert.Equal((false, false, false), disabled.Status());
  }

  [Fact]
  public async Task ChangesTakeEffectOnRestartAndDisableKeepsProfileState() {
    using var database = new TemporaryDatabase();
    string root = Path.GetDirectoryName(database.Path)!;
    string extension = Path.Combine(root, "bitwarden", "extension");
    Directory.CreateDirectory(Path.Combine(extension, "popup"));
    await File.WriteAllTextAsync(Path.Combine(extension, "manifest.json"), "{}");
    await File.WriteAllTextAsync(Path.Combine(extension, "background.js"), "");
    await File.WriteAllTextAsync(Path.Combine(extension, "popup", "index.html"), "");
    string profile = Path.Combine(root, "playwright-profile");
    Directory.CreateDirectory(profile);
    string sentinel = Path.Combine(profile, "sentinel");
    await File.WriteAllTextAsync(sentinel, "disposable profile data");

    // An installed extension with no old enabled marker was an opt-out in older builds.
    using var legacy = new BitwardenInstallation(root);
    Assert.Equal((false, false, true), legacy.Status());
    await legacy.ChangeAsync(true, CancellationToken.None);
    Assert.Equal((true, false, true), legacy.Status());

    using var current = new BitwardenInstallation(root);
    Assert.Equal((true, true, true), current.Status());
    await current.ChangeAsync(false, CancellationToken.None);
    Assert.Equal((false, true, true), current.Status());

    using var disabled = new BitwardenInstallation(root);
    Assert.Equal((false, false, true), disabled.Status());
    await disabled.ChangeAsync(true, CancellationToken.None);
    Assert.Equal((true, false, true), disabled.Status());

    using var next = new BitwardenInstallation(root);
    Assert.Equal(extension, next.ActiveExtensionPath);
    Assert.Equal((true, true, true), next.Status());
    Assert.True(File.Exists(sentinel));
    Assert.True(Directory.Exists(extension));
  }
}
