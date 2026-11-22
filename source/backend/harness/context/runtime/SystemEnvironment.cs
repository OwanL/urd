using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Urd.Core.Context;

internal static class SystemEnvironment {
  private static readonly string operatingSystem = DescribeOperatingSystem();

  internal static string Describe(string? workingDirectory, DateTimeOffset now, TimeZoneInfo zone) {
    string zoneId = zone.HasIanaId || !TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out string? ianaId)
        ? zone.Id : ianaId;
    DateTimeOffset localTime = TimeZoneInfo.ConvertTime(now, zone);
    var lines = new List<string>();
    if (workingDirectory is not null)
      lines.Add($"Working directory: {workingDirectory}");
    lines.Add($"Operating system: {operatingSystem}");
    lines.Add($"Time: {localTime.ToString("yyyy-MM-dd HH:mm zzz", CultureInfo.InvariantCulture)} ({zoneId}) as of session creation");
    return string.Join("\n", lines);
  }

  private static string DescribeOperatingSystem() {
    if (!OperatingSystem.IsWindows())
      return RuntimeInformation.OSDescription;

    try {
      using RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
      string? product = key?.GetValue("ProductName") as string;
      string? version = key?.GetValue("DisplayVersion") as string;
      string? build = key?.GetValue("CurrentBuild") as string;
      if (string.IsNullOrWhiteSpace(product))
        return RuntimeInformation.OSDescription;
      if (int.TryParse(build, NumberStyles.Integer, CultureInfo.InvariantCulture, out int buildNumber)
          && buildNumber >= 22000 && product.StartsWith("Windows 10", StringComparison.Ordinal))
        product = string.Concat("Windows 11", product.AsSpan("Windows 10".Length));
      if (!string.IsNullOrWhiteSpace(version))
        product += $" {version}";
      if (!string.IsNullOrWhiteSpace(build))
        product += $" (build {build})";
      return product;
    } catch (Exception) {
      return RuntimeInformation.OSDescription;
    }
  }
}
