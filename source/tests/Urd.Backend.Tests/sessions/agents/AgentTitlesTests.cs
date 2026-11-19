using Urd.Core.Sessions;
using Xunit;

namespace Urd.Backend.Tests.Sessions;

public sealed class AgentTitlesTests {
  [Fact]
  public void PlaceholdersAvoidFinalTitlesAndRemainDistinctFromAddresses() {
    Guid first = Guid.NewGuid();
    Guid second = Guid.NewGuid();
    Guid final = Guid.NewGuid();
    Guid generating = Guid.NewGuid();
    var titles = AgentTitles.ForListing([
      (first, null, false), (second, null, false), (final, "new session", false), (generating, null, true)
    ]);

    Assert.Equal(new AgentTitle("new session (2)", true), titles[first]);
    Assert.Equal(new AgentTitle("new session (3)", true), titles[second]);
    Assert.Equal(new AgentTitle("new session", false), titles[final]);
    Assert.Equal(new AgentTitle("generating title", true), titles[generating]);
  }
}
