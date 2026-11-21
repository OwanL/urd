using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Urd.Server;

// Published together so admission never observes a partially updated listener set.
internal sealed class LanAccess {
  private LanAccessSnapshot snapshot = new(false, ImmutableArray<string>.Empty);

  public LanAccessSnapshot Snapshot => Volatile.Read(ref snapshot);

  public void Replace(IEnumerable<IPAddress> addresses) {
    ImmutableArray<string> boundAddresses = addresses
      .Where(IsPrivateIPv4)
      .Select(address => address.ToString())
      .Distinct(StringComparer.Ordinal)
      .OrderBy(address => address, StringComparer.Ordinal)
      .ToImmutableArray();
    Volatile.Write(ref snapshot, new LanAccessSnapshot(true, boundAddresses));
  }

  public void Disable() => Volatile.Write(ref snapshot, new LanAccessSnapshot(false, ImmutableArray<string>.Empty));

  public static ImmutableArray<IPAddress> AvailableAddresses() => NetworkInterface.GetAllNetworkInterfaces()
    .Where(network => network.OperationalStatus == OperationalStatus.Up && network.NetworkInterfaceType != NetworkInterfaceType.Loopback)
    .SelectMany(network => network.GetIPProperties().UnicastAddresses)
    .Select(address => address.Address)
    .Where(IsPrivateIPv4)
    .Distinct()
    .OrderBy(address => address.ToString(), StringComparer.Ordinal)
    .ToImmutableArray();

  [SuppressMessage("Security", "S5332", Justification =
      "LAN hosting deliberately serves explicit private-IPv4 URLs over plain HTTP in trusted-network mode.")]
  public static string Origin(string address, int port) => $"http://{address}:{port}";

  public static bool IsPrivateIPv4(IPAddress address) {
    if (address.AddressFamily != AddressFamily.InterNetwork)
      return false;

    byte[] bytes = address.GetAddressBytes();
    return bytes[0] == 10
      || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
      || (bytes[0] == 192 && bytes[1] == 168)
      || (bytes[0] == 169 && bytes[1] == 254);
  }
}

internal sealed record LanAccessSnapshot(bool Enabled, ImmutableArray<string> Addresses) {
  public ImmutableArray<string> Origins(int port) => Addresses
    .Select(address => LanAccess.Origin(address, port))
    .ToImmutableArray();
}
