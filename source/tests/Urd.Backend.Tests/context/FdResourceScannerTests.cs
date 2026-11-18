using Urd.Core.Context;
using Xunit;

namespace Urd.Backend.Tests.Context;

public sealed class FdResourceScannerTests {
  [Theory]
  [InlineData("agents.MD", true)]
  [InlineData("AGENT-INSTRUCTION.md", true)]
  [InlineData("INSTRUCTIONS_FOR_AGENTS.md", true)]
  [InlineData("SKILL.md", true)]
  [InlineData("CLAUDE.md", false)]
  [InlineData("my-AGENTS.md", false)]
  public void MatchesResourceBasenames(string name, bool expected) =>
      Assert.Equal(expected, FdResourceScanner.IsResourcePath(Path.Combine("somewhere", name)));

  [Fact]
  public async Task FindsHiddenAndIgnoredFilesWithinDirectory() {
    var root = Path.Combine(Path.GetTempPath(), $"urd-fd-{Guid.NewGuid():N}");
    try {
      Directory.CreateDirectory(Path.Combine(root, ".hidden"));
      await File.WriteAllTextAsync(Path.Combine(root, ".gitignore"), "*\n");
      await File.WriteAllTextAsync(Path.Combine(root, ".hidden", "skill.MD"), "");
      await File.WriteAllTextAsync(Path.Combine(root, "AGENTS.md"), "");
      await File.WriteAllTextAsync(Path.Combine(root, "CLAUDE.md"), "");
      var paths = await new FdResourceScanner().FindPathsAsync(root, CancellationToken.None);
      Assert.Equal(2, paths.Length);
      Assert.Contains(Path.Combine(root, ".hidden", "skill.MD"), paths);
      Assert.Contains(Path.Combine(root, "AGENTS.md"), paths);
    } finally {
      Directory.Delete(root, recursive: true);
    }
  }

  [Fact]
  public void PlansLocalMountsAndPrunesVirtualAndRemoteMounts() {
    const string mounts = """
        1 0 8:1 / / rw - ext4 /dev/root rw
        2 1 0:2 / /proc rw - proc proc rw
        3 1 8:2 / /home rw - ext4 /dev/home rw
        4 3 0:3 / /home/remote rw - nfs server:/share rw
        5 4 8:3 / /home/remote/nested rw - ext4 /dev/nested rw
        6 1 0:4 / /run/user\040name rw - tmpfs tmpfs rw
        """;
    var plan = ResourceMountPolicy.Plan("/", mounts);
    Assert.Equal(new[] { "/", "/home" }, plan.Select(scan => scan.Root));
    Assert.Contains("proc", plan[0].Excludes);
    Assert.Contains("home", plan[0].Excludes);
    Assert.Contains("run/user name", plan[0].Excludes);
    Assert.Contains("remote", plan[1].Excludes);
    Assert.Empty(ResourceMountPolicy.Plan("/home/remote", mounts));
  }

  [Fact]
  public void PlansMacMountsWithoutMirrorsOrRemoteTrees() {
    const string mounts = """
        /dev/disk1s1 on / (apfs, sealed, local, read-only)
        /dev/disk1s2 on /System/Volumes/Data (apfs, local, journaled)
        /dev/disk2s1 on /Volumes/Work Drive (apfs, local, journaled)
        server:/share on /Volumes/Work Drive/remote (nfs, nodev, nosuid)
        /dev/disk3s1 on /Volumes/Work Drive/remote/nested (apfs, local)
        map auto_home on /System/Volumes/Data/home (autofs, automounted)
        /dev/disk4s1 on /Volumes/duplicate (apfs, local)
        /dev/disk5s1 on /Volumes/duplicate (apfs, local)
        """;
    var plan = ResourceMountPolicy.PlanMac("/", mounts);
    Assert.Equal(new[] { "/", "/Users", "/Volumes/Work Drive" }, plan.Select(scan => scan.Root));
    Assert.Contains("System/Volumes/Data", plan[0].Excludes);
    Assert.Contains("Users", plan[0].Excludes);
    Assert.Contains("Volumes/duplicate", plan[0].Excludes);
    Assert.Contains("remote", plan[2].Excludes);
    Assert.Empty(ResourceMountPolicy.PlanMac("/Volumes/Work Drive/remote", mounts));
    Assert.Empty(ResourceMountPolicy.PlanMac("/System/Volumes/Data/Users", mounts));
    Assert.Single(ResourceMountPolicy.PlanMac("/Users/person", mounts));
  }

  [Fact]
  public void RejectsMalformedMacMountInformation() {
    Assert.Throws<IOException>(() => ResourceMountPolicy.ParseMac("not mount output"));
    Assert.Throws<IOException>(() => ResourceMountPolicy.ParseMac(""));
  }

  [Fact]
  public void RejectsMalformedMountInformation() {
    Assert.Throws<IOException>(() => ResourceMountPolicy.Parse("not mountinfo"));
  }

  [Fact]
  public async Task HonorsCancellationBeforeStarting() {
    using var cancellation = new CancellationTokenSource();
    await cancellation.CancelAsync();
    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new FdResourceScanner().FindPathsAsync(Path.GetTempPath(), cancellation.Token));
  }
}
