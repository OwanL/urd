using System.Security.Cryptography;
using System.Text;

namespace Urd.Core.MarkdownRendering;

internal sealed record MarkdownImageTarget(string Key, string Destination) {
  internal static MarkdownImageTarget? FromDestination(string? destination) {
    if (destination is not { Length: > 0 and <= 4096 }
        || destination.Any(char.IsControl) || destination.Contains('#') || destination.Contains('?'))
      return null;

    string path = Uri.UnescapeDataString(destination).Replace('\\', '/');
    if (path.StartsWith("//", StringComparison.Ordinal) || path.Any(char.IsControl))
      return null;
    int colon = path.IndexOf(':');
    if (colon >= 0 && (colon != 1 || !char.IsAsciiLetter(path[0]) || path.Length <= 2 || path[2] != '/'
        || path.AsSpan(2).Contains(':')))
      return null;

    return new(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(path))), path);
  }

  internal static bool IsKey(string? key) => key is { Length: 64 }
      && key.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
