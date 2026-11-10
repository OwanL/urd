using Urd.Core.Tools;
using Xunit;

namespace Urd.Backend.Tests.Tools;

// Discovery must not silently fall back from an explicitly configured executable.
public sealed class GitBashTests {
  [Fact]
  public void ConfiguredPathIsResolvedAndMissingExecutableIsActionable() {
    if (!OperatingSystem.IsWindows()) {
      Assert.Throws<PlatformNotSupportedException>(() => new GitBash());
      return;
    }
    string directory = Directory.CreateTempSubdirectory("urd-git-bash-").FullName;
    try {
      string executable = Path.Combine(directory, "sp ace ☃ bash.exe");
      File.WriteAllBytes(executable, []);
      Assert.Equal(executable, new GitBash(executable).Executable);
      File.Delete(executable);
      FileNotFoundException failure = Assert.Throws<FileNotFoundException>(() => new GitBash(executable));
      Assert.Equal(executable, failure.FileName);
      Assert.Contains("Configured Git Bash", failure.Message, StringComparison.Ordinal);
    } finally {
      Directory.Delete(directory, recursive: true);
    }
  }
}
