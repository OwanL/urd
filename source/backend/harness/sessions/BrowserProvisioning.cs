namespace Urd.Core.Sessions;

// Host-wide browser provisioning stages published while a first-use Chromium install is in flight.
public enum BrowserProvisioningStage { Downloading, Ready, Failed }

// Detail carries the installer's report while downloading; terminal stages have none.
public sealed record BrowserProvisioningNotice(BrowserProvisioningStage Stage, string? Detail);

// Forwards browser install reports synchronously so notices keep their report order.
internal sealed class BrowserSetupProgress(Action<string> report) : IProgress<string> {
  public void Report(string value) => report(value);
}
