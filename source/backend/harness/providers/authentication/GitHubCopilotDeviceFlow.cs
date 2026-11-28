namespace Urd.Core.Providers;

// Only the user code goes to the browser; the device code is an OAuth credential.
internal sealed record GitHubCopilotDeviceFlow(string DeviceCode, string UserCode, Uri VerificationUrl,
    DateTimeOffset ExpiresAt, DateTimeOffset FirstPollAt, int IntervalSeconds) {
  public override string ToString() => "[GitHub Copilot device flow]";
}
