using Urd.Core.Context;
using Xunit;

namespace Urd.Backend.Tests.Context;

public sealed class SkillsGuidanceTests {
  [Fact]
  public void FormatsOnlyTheSuppliedSelectedSkills() {
    var skills = new[] {
      new DiscoveredSkill("diagnose", "Inspect <state> & logs.", Path.GetFullPath("diagnose/SKILL.md")),
    };

    string text = SkillsGuidance.Compose(skills)!;

    Assert.Contains("<name>diagnose</name>", text, StringComparison.Ordinal);
    Assert.Contains("<description>Inspect &lt;state&gt; &amp; logs.</description>", text, StringComparison.Ordinal);
    Assert.Contains($"<location>{System.Security.SecurityElement.Escape(skills[0].Path)}</location>", text, StringComparison.Ordinal);
    Assert.Null(SkillsGuidance.Compose([]));
  }
}
