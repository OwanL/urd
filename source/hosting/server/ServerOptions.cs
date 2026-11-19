using System.Globalization;

namespace Urd.Server;

// Validated startup inputs; normalization does not create or open the data root.
internal sealed record ServerOptions {
  public string DataRoot { get; }
  public int Port { get; }
  public string? GitBashPath { get; }
  public bool ShowTray { get; }
  public string? LauncherPipe { get; }

  public ServerOptions(string dataRoot, int port, string? gitBashPath = null, bool showTray = false,
      string? launcherPipe = null) {
    if (string.IsNullOrWhiteSpace(dataRoot) || !Path.IsPathFullyQualified(dataRoot))
      throw new ArgumentException("Data root must be a nonempty, fully qualified path.", nameof(dataRoot));
    if (port is < 0 or > 65535)
      throw new ArgumentException("Port must be between 0 and 65535.", nameof(port));

    if (gitBashPath is not null &&
        (string.IsNullOrWhiteSpace(gitBashPath) || !Path.IsPathFullyQualified(gitBashPath)))
      throw new ArgumentException("Git Bash requires a fully qualified executable path.", nameof(gitBashPath));

    if (launcherPipe is not null && (launcherPipe.Length is < 1 or > 80 ||
        launcherPipe.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_')))
      throw new ArgumentException("Launcher pipe name must contain 1 to 80 ASCII letters, digits, hyphens or underscores.", nameof(launcherPipe));

    DataRoot = Path.GetFullPath(dataRoot);
    Port = port;
    GitBashPath = gitBashPath is null ? null : Path.GetFullPath(gitBashPath);
    ShowTray = showTray;
    LauncherPipe = launcherPipe;
  }

  public static ServerOptions Parse(string[] args) {
    ArgumentNullException.ThrowIfNull(args);
    string? dataRoot = null;
    int? port = null;
    string? gitBashPath = null;
    bool? showTray = null;
    string? launcherPipe = null;

    int index = 0;
    while (index < args.Length) {
      string option = args[index];
      switch (option) {
        case "--no-tray":
          if (showTray is not null)
            throw new ArgumentException("Duplicate --no-tray option.", nameof(args));
          showTray = false;
          break;
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
        case "--git-bash":
          if (gitBashPath is not null)
            throw new ArgumentException("Duplicate --git-bash option.", nameof(args));
          gitBashPath = ReadValue(args, index);
          break;
        case "--launcher-pipe":
          if (launcherPipe is not null)
            throw new ArgumentException("Duplicate --launcher-pipe option.", nameof(args));
          launcherPipe = ReadValue(args, index);
          break;
        default:
          throw new ArgumentException($"Unknown option '{option}'.", nameof(args));
      }
      index += option == "--no-tray" ? 1 : 2;
    }

    if (dataRoot is null)
      throw new ArgumentException("Missing required --data-root option.", nameof(args));
    if (port is null)
      throw new ArgumentException("Missing required --port option.", nameof(args));
    return new ServerOptions(dataRoot, port.Value, gitBashPath, showTray ?? OperatingSystem.IsWindows(), launcherPipe);
  }

  private static string ReadValue(string[] args, int index) {
    string? value = index + 1 < args.Length ? args[index + 1] : null;
    if (value is null || value.StartsWith("--", StringComparison.Ordinal))
      throw new ArgumentException($"Missing value for {args[index]}.", nameof(args));
    return value;
  }
}
