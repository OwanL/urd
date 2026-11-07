using System.Globalization;

namespace Urd.Server;

// Validated startup inputs; normalization does not create or open the data root.
internal sealed record ServerOptions {
  public string DataRoot { get; }
  public int Port { get; }

  public ServerOptions(string dataRoot, int port) {
    if (string.IsNullOrWhiteSpace(dataRoot) || !Path.IsPathFullyQualified(dataRoot))
      throw new ArgumentException("Data root must be a nonempty, fully qualified path.", nameof(dataRoot));
    if (port is < 0 or > 65535)
      throw new ArgumentException("Port must be between 0 and 65535.", nameof(port));

    DataRoot = Path.GetFullPath(dataRoot);
    Port = port;
  }

  public static ServerOptions Parse(string[] args) {
    ArgumentNullException.ThrowIfNull(args);
    string? dataRoot = null;
    int? port = null;

    for (int index = 0; index < args.Length; index += 2) {
      string option = args[index];
      switch (option) {
        case "--data-root":
          if (dataRoot is not null)
            throw new ArgumentException("Duplicate --data-root option.", nameof(args));
          dataRoot = ReadValue(args, index);
          break;
        case "--port":
          if (port is not null)
            throw new ArgumentException("Duplicate --port option.", nameof(args));
          if (!int.TryParse(ReadValue(args, index), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedPort))
            throw new ArgumentException("--port must be an integer between 0 and 65535.", nameof(args));
          port = parsedPort;
          break;
        default:
          throw new ArgumentException($"Unknown option '{option}'.", nameof(args));
      }
    }

    if (dataRoot is null)
      throw new ArgumentException("Missing required --data-root option.", nameof(args));
    if (port is null)
      throw new ArgumentException("Missing required --port option.", nameof(args));
    return new ServerOptions(dataRoot, port.Value);
  }

  private static string ReadValue(string[] args, int index) {
    string? value = index + 1 < args.Length ? args[index + 1] : null;
    if (value is null || value.StartsWith("--", StringComparison.Ordinal))
      throw new ArgumentException($"Missing value for {args[index]}.", nameof(args));
    return value;
  }
}
