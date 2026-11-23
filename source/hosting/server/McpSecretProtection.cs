using System.Runtime.Versioning;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace Urd.Server;

// Protects MCP credentials with a durable key ring bound to the current Windows user.
internal sealed class McpSecretProtection {
  private const string ApplicationName = "Urd.McpSecretProtection";
  private const string Purpose = "Urd.McpSecretProtection.v1";
  private readonly IDataProtector protector;

  internal McpSecretProtection(string dataRoot) {
    ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
    if (!Path.IsPathFullyQualified(dataRoot))
      throw new ArgumentException("Data root must be a fully qualified path.", nameof(dataRoot));
    if (!OperatingSystem.IsWindows())
      throw new PlatformNotSupportedException("MCP secret protection requires Windows DPAPI.");

    string keyDirectory = Path.Combine(Path.GetFullPath(dataRoot), "mcp-secrets", "keys");
    Directory.CreateDirectory(keyDirectory);
    protector = CreateProvider(new DirectoryInfo(keyDirectory)).CreateProtector(Purpose);

    const string probe = "MCP secret protection availability check";
    if (protector.Unprotect(protector.Protect(probe)) != probe)
      throw new CryptographicException("MCP secret protection availability check failed.");
  }

  [SupportedOSPlatform("windows")]
  private static IDataProtectionProvider CreateProvider(DirectoryInfo keyDirectory) =>
      DataProtectionProvider.Create(keyDirectory,
          builder => builder.SetApplicationName(ApplicationName).ProtectKeysWithDpapi());

  internal string Protect(string value) {
    ArgumentNullException.ThrowIfNull(value);
    return protector.Protect(value);
  }

  internal string Unprotect(string value) {
    ArgumentNullException.ThrowIfNull(value);
    return protector.Unprotect(value);
  }
}
