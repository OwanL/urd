using Urd.Backend.Tests.Support;
using Urd.Core.Execution;
using Urd.Core.MarkdownRendering;
using Urd.Core.Storage;
using Xunit;
using static Urd.Backend.Tests.Support.TestAsync;

namespace Urd.Backend.Tests.MarkdownRendering;

public sealed class MarkdownImageImportsTests {
  [Fact]
  public async Task LiteralImageMarkerStopsTriggeringPrefixParsesUntilClose() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    var snapshots = new List<string>();
    using var imports = new MarkdownImageImports(store.MarkdownImageFiles, source => {
      snapshots.Add(source);
      return MarkdownParser.ReadImageTargets(source);
    });
    var execution = new ExecutionIdentity(Guid.NewGuid(), Guid.NewGuid(), 0);
    var observer = imports.ObserveExecution(execution, null);
    Guid request = Guid.NewGuid(), response = Guid.NewGuid();
    const string example = "`![example](not-an-image.png)`\n";
    const string suffix = "More text.\n";

    await AppendAsync(example);
    Assert.Equal(example, Assert.Single(snapshots));
    for (int chunk = 0; chunk < 6; chunk++) await AppendAsync(suffix);
    int afterWarmup = snapshots.Count;
    for (int chunk = 0; chunk < 4; chunk++) await AppendAsync(suffix);
    Assert.Equal(afterWarmup, snapshots.Count);

    await observer.JoinAsync().WaitAsync(TestTimeout);
    Assert.Equal(afterWarmup + 1, snapshots.Count);
    Assert.Equal(example + string.Concat(Enumerable.Repeat(suffix, 10)), snapshots[^1]);

    async Task AppendAsync(string text) {
      observer.Observe(new(request, response, 0, text, false));
      await imports.WaitForPendingAsync(execution.SessionId, execution.ExecutionId, response, CancellationToken.None)
          .WaitAsync(TestTimeout);
    }
  }
}
