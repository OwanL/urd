using System.Text;
using Urd.Core.Tools;
using Xunit;

namespace Urd.Backend.Tests.Tools;

public sealed class SnapshotTreeDepthViewTests {
  [Fact]
  public void SelectsDeepestDepthAtExactCombinedByteLimit() {
    const string snapshot = "- document [ref=e1]:\n  - group:\n    - button: café\n  - link: home\n- text: end";
    int bytes = Encoding.UTF8.GetByteCount(snapshot);

    Assert.Equal(snapshot, SnapshotTree.DepthView(snapshot, bytes, 5));
    Assert.Equal("- document [ref=e1]:\n  - group:\n  - link: home\n- text: end",
      SnapshotTree.DepthView(snapshot, bytes - 1, 5));
  }

  [Fact]
  public void HonorsLineBudgetAndUnboundedByteBudget() {
    const string snapshot = "- document [ref=e1]:\n  - group:\n    - button: ok";

    Assert.Equal("- document [ref=e1]:\n  - group:", SnapshotTree.DepthView(snapshot, long.MaxValue, 2));
    Assert.Equal(snapshot, SnapshotTree.DepthView(snapshot, long.MaxValue, 3));
  }

  [Fact]
  public void RequiresKeptAncestorRefForAnOmittedSubtree() {
    const string unrelatedRef = "- document:\n  - group:\n    - button [ref=e1]\n- link [ref=e2]";
    const string ancestorRef = "- document [ref=e1]:\n  - group:\n    - button";

    Assert.Null(SnapshotTree.DepthView(unrelatedRef, long.MaxValue, 3));
    Assert.Equal("- document [ref=e1]:\n  - group:",
      SnapshotTree.DepthView(ancestorRef, long.MaxValue, 2));
  }

  [Fact]
  public void ReturnsNullWhenNoCompleteDepthFitsOrSnapshotIsInvalid() {
    Assert.Null(SnapshotTree.DepthView("- document\n- text", 10, 2));
    Assert.Null(SnapshotTree.DepthView("- document", 100, 0));
    Assert.Null(SnapshotTree.DepthView("invalid", 100, 10));
  }
}
