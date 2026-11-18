using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Urd.Core.Tools;

internal static class WebBrowserAddress {
  // Only a public HTTP origin can be submitted to the browser or proxy.
  public static bool IsHttpUrl(Uri? url) => url is { IsAbsoluteUri: true, UserInfo.Length: 0, Port: > 0 }
      && url.Scheme is "http" or "https" && url.Host.Length != 0;

  public static bool CouldBePublic(Uri url) => IPAddress.TryParse(url.DnsSafeHost, out IPAddress? literal)
      ? IsPublic(literal) : IsPublicName(url.IdnHost);

  public static bool IsPublic(IPAddress address) {
    if (address.IsIPv4MappedToIPv6)
      address = address.MapToIPv4();
    if (address.AddressFamily == AddressFamily.InterNetworkV6 && address.ScopeId != 0)
      return false;

    byte[] bytes = address.GetAddressBytes();
    return address.AddressFamily switch {
      AddressFamily.InterNetwork => bytes[0] is > 0 and < 224 and not (10 or 127)
          && !(bytes[0] == 100 && bytes[1] is >= 64 and <= 127)
          && !(bytes[0] == 169 && bytes[1] == 254)
          && !(bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
          && !(bytes[0] == 192 && (bytes[1] == 168 || bytes[1] == 0 && bytes[2] is 0 or 2
              || bytes[1] == 88 && bytes[2] == 99))
          && !(bytes[0] == 198 && (bytes[1] is 18 or 19 || bytes[1] == 51 && bytes[2] == 100))
          && !(bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113),
      AddressFamily.InterNetworkV6 => (bytes[0] & 0xE0) == 0x20
          && !(bytes[0] == 0x20 && bytes[1] == 1 && (bytes[2] < 2 || bytes[2] == 0x0D && bytes[3] == 0xB8))
          && !(bytes[0] == 0x20 && bytes[1] == 2)
          && !(bytes[0] == 0x3F && bytes[1] == 0xFF && bytes[2] < 0x10),
      _ => false
    };
  }

  public static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellation) {
    string host = context.DnsEndPoint.Host;
    // Resolve once; Socket must not perform a second name lookup after the policy check.
    IPAddress[] addresses = [];
    if (IPAddress.TryParse(host, out IPAddress? literal))
      addresses = [literal];
    else if (IsPublicName(host))
      addresses = await Dns.GetHostAddressesAsync(host.TrimEnd('.') + ".", cancellation);
    HashSet<IPAddress> localAddresses = NetworkInterface.GetAllNetworkInterfaces()
        .SelectMany(network => network.GetIPProperties().UnicastAddresses)
        .Select(entry => entry.Address).ToHashSet();
    RequirePublicAddresses(addresses, localAddresses);

    SocketException? lastFailure = null;
    foreach (IPAddress address in addresses) {
      var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
      try {
        await socket.ConnectAsync(address, context.DnsEndPoint.Port, cancellation);
        return new NetworkStream(socket, ownsSocket: true);
      } catch (SocketException failure) {
        lastFailure = failure;
        socket.Dispose();
      } catch {
        socket.Dispose();
        throw;
      }
    }
    throw new HttpRequestException("Could not connect to a public address.", lastFailure);
  }

  internal static void RequirePublicAddresses(IPAddress[] addresses, HashSet<IPAddress> localAddresses) {
    if (addresses.Length == 0 || addresses.Any(address => !IsPublic(address)
        || localAddresses.Contains(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address)))
      throw new WebBrowserDestinationException();
  }

  private static bool IsPublicName(string host) {
    string name = host.TrimEnd('.');
    return name.Contains('.', StringComparison.Ordinal)
        && !name.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
        && !name.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
        && !name.EndsWith(".internal", StringComparison.OrdinalIgnoreCase)
        && !name.Equals("home.arpa", StringComparison.OrdinalIgnoreCase)
        && !name.EndsWith(".home.arpa", StringComparison.OrdinalIgnoreCase);
  }
}

internal sealed class WebBrowserDestinationException() : HttpRequestException("Destination is not public.");
