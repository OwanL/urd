using System.Globalization;
using Urd.Server;
using Xunit;

namespace Urd.Backend.Tests.Hosting;

// Startup options require explicit, validated inputs without touching the data root.
public sealed class ServerOptionsTests {
  [Theory]
  [InlineData(0)]
  [InlineData(43127)]
  [InlineData(65535)]
  public void ConstructorNormalizesFullyQualifiedPathWithSpaces(int port) {
    string dataRoot = Path.Combine(DataRoot(), "..", "normalized data");

    var options = new ServerOptions(dataRoot, port);

    Assert.Equal(Path.GetFullPath(dataRoot), options.DataRoot);
    Assert.Equal(port, options.Port);
  }

  [Theory]
  [InlineData(0, false)]
  [InlineData(0, true)]
  [InlineData(65535, false)]
  [InlineData(65535, true)]
  public void ParsesBothFlagOrderingsIncludingPortBoundaries(int port, bool portFirst) {
    string dataRoot = Path.Combine(DataRoot(), ".", "nested data");
    string portValue = port.ToString(CultureInfo.InvariantCulture);
    string[] args = portFirst
        ? ["--port", portValue, "--data-root", dataRoot]
        : ["--data-root", dataRoot, "--port", portValue];

    ServerOptions options = ServerOptions.Parse(args);

    Assert.Equal(Path.GetFullPath(dataRoot), options.DataRoot);
    Assert.Equal(port, options.Port);
  }

  [Theory]
  [InlineData(null)]
  [InlineData("")]
  [InlineData(" ")]
  [InlineData("\t")]
  [InlineData("data")]
  [InlineData("../data")]
  public void RejectsBlankAndRelativePathsForConstructorAndCli(string? dataRoot) {
    Assert.Throws<ArgumentException>(() => new ServerOptions(dataRoot!, 0));
    Assert.Throws<ArgumentException>(() => ServerOptions.Parse(["--data-root", dataRoot!, "--port", "0"]));
  }

  [Fact]
  public void RejectsRootedButNotFullyQualifiedPathForCurrentOs() {
    string dataRoot = OperatingSystem.IsWindows() ? @"\urd data" : "urd data";

    Assert.Throws<ArgumentException>(() => new ServerOptions(dataRoot, 0));
    Assert.Throws<ArgumentException>(() => ServerOptions.Parse(["--data-root", dataRoot, "--port", "0"]));
  }

  [Theory]
  [InlineData(-1)]
  [InlineData(65536)]
  [InlineData(int.MinValue)]
  [InlineData(int.MaxValue)]
  public void RejectsOutOfRangePortsForConstructorAndCli(int port) {
    Assert.Throws<ArgumentException>(() => new ServerOptions(DataRoot(), port));
    Assert.Throws<ArgumentException>(() => ServerOptions.Parse([
      "--data-root", DataRoot(), "--port", port.ToString(CultureInfo.InvariantCulture)
    ]));
  }

  [Theory]
  [InlineData("")]
  [InlineData(" ")]
  [InlineData("not-a-number")]
  [InlineData("1.5")]
  [InlineData("2147483648")]
  public void RejectsNonIntegerPorts(string port) {
    ArgumentException error = Assert.Throws<ArgumentException>(() =>
        ServerOptions.Parse(["--data-root", DataRoot(), "--port", port]));

    Assert.Contains("--port", error.Message, StringComparison.Ordinal);
  }

  [Theory]
  [MemberData(nameof(InvalidArguments))]
  public void RejectsMissingDuplicateAndUnknownFlags(string[] args, string expectedMessage) {
    ArgumentException error = Assert.Throws<ArgumentException>(() => ServerOptions.Parse(args));

    Assert.Contains(expectedMessage, error.Message, StringComparison.Ordinal);
  }

  public static TheoryData<string[], string> InvalidArguments() {
    string dataRoot = DataRoot();
    return new TheoryData<string[], string> {
      { [], "--data-root" },
      { ["--port", "0"], "--data-root" },
      { ["--data-root", dataRoot], "--port" },
      { ["--data-root"], "Missing value for --data-root" },
      { ["--port"], "Missing value for --port" },
      { ["--data-root", "--port", "0"], "Missing value for --data-root" },
      { ["--port", "--data-root", dataRoot], "Missing value for --port" },
      { ["--data-root", dataRoot, "--port", "0", "--data-root", dataRoot], "Duplicate --data-root" },
      { ["--port", "0", "--data-root", dataRoot, "--port", "1"], "Duplicate --port" },
      { ["--unknown", "value"], "Unknown option '--unknown'" },
      { ["--data-root", dataRoot, "--port", "0", "extra"], "Unknown option 'extra'" },
      { ["--data-root", dataRoot, "--port=0"], "Unknown option '--port=0'" }
    };
  }

  private static string DataRoot() => Path.Combine(Path.GetTempPath(), "urd server options", "data");
}
