using Urd.Core.Sessions;
using Xunit;

namespace Urd.Backend.Tests.Sessions;

// Recipient names classify against a listing snapshot: one final title resolves, multiple
// finals are ambiguous and placeholders never resolve. Storage enforces title uniqueness, so
// the ambiguity case is constructed directly against the matcher.
public sealed class AgentAddressesTests {
  [Fact]
  public void AnExactFinalTitleResolves() {
    Guid final = Guid.NewGuid();
    var titles = new Dictionary<Guid, AgentTitle> {
      [final] = new("fix login", false),
      [Guid.NewGuid()] = new("new session", true)
    };

    Assert.Equal(new RecipientLookup(final, false, false), AgentAddresses.Match(titles, "fix login"));
  }

  [Fact]
  public void MultipleFinalMatchesAreAmbiguous() {
    Guid first = Guid.NewGuid();
    Guid second = Guid.NewGuid();
    var titles = new Dictionary<Guid, AgentTitle> {
      [first] = new("fix login", false),
      [second] = new("fix login", false)
    };

    Assert.Equal(new RecipientLookup(null, true, false), AgentAddresses.Match(titles, "fix login"));
  }

  [Fact]
  public void PlaceholdersNeverResolve() {
    Guid placeholder = Guid.NewGuid();
    var titles = new Dictionary<Guid, AgentTitle> {
      [placeholder] = new("new session", true)
    };

    Assert.Equal(new RecipientLookup(null, false, true), AgentAddresses.Match(titles, "new session"));
  }

  [Fact]
  public void AnUnknownNameClassifiesAsUnknown() {
    var titles = new Dictionary<Guid, AgentTitle> {
      [Guid.NewGuid()] = new("fix login", false)
    };

    Assert.Equal(new RecipientLookup(null, false, false), AgentAddresses.Match(titles, "nobody"));
  }
}
