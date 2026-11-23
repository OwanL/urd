using System.Text.Json;
using Urd.Backend.Tests.Support;
using Urd.Core.Preferences;
using Urd.Core.Storage;
using Xunit;

namespace Urd.Backend.Tests.Storage;

public sealed class SourceIgnoreRulesStorageTests {
  [Fact]
  public void PersistsNormalizedRulesWithoutSharingMutableArrays() {
    using var database = new TemporaryDatabase();
    string root = Path.GetDirectoryName(database.Path)!;
    string instruction = Path.Combine(root, "notes", "AGENTS.md");
    string skills = Path.Combine(root, "skills");
    var rules = new SourceIgnoreRules {
      Instructions = [Path.Combine(root, "project", "..", "notes", "AGENTS.md"), instruction],
      Skills = [skills + Path.DirectorySeparatorChar],
      Filenames = ["agent-Instructions.MD", "AGENT_INSTRUCTIONS.md", "AGENT-INSTRUCTION.md"]
    };

    using (UrdStore store = UrdStore.Open(database.Path)) {
      var preferences = new PreferenceManager(store);
      preferences.SetSourceIgnores(rules);
      rules.Instructions[0] = "mutated";
      rules.Skills[0] = "mutated";
      rules.Filenames[0] = "mutated";
      SourceIgnoreRules snapshot = preferences.Read().SourceIgnores;
      Assert.Equal(instruction, Assert.Single(snapshot.Instructions));
      Assert.Equal(skills, Assert.Single(snapshot.Skills));
      Assert.Equal(new[] { "AGENT_INSTRUCTIONS.md", "AGENT_INSTRUCTION.md" }, snapshot.Filenames);
      snapshot.Instructions[0] = "mutated";
      snapshot.Skills[0] = "mutated";
      snapshot.Filenames[0] = "mutated";
      preferences.SetCompactionTriggerTokenCount(4096);
    }

    using UrdStore reopened = UrdStore.Open(database.Path);
    UserPreferences saved = new PreferenceManager(reopened).Read();
    Assert.Equal(4096, saved.CompactionTriggerTokenCount);
    SourceIgnoreRules ignores = saved.SourceIgnores;
    Assert.True(ignores.IsIgnored(instruction, skill: false));
    Assert.False(ignores.IsIgnored(Path.Combine(root, "other", "AGENTS.md"), skill: false));
    Assert.False(ignores.IsIgnored(instruction, skill: true));
    Assert.True(ignores.IsIgnored(skills, skill: true));
    Assert.True(ignores.IsIgnored(Path.Combine(skills, "nested", "SKILL.md"), skill: true));
    Assert.False(ignores.IsIgnored(Path.Combine(skills + "-other", "SKILL.md"), skill: true));
    Assert.False(ignores.IsIgnored(Path.Combine(skills, "..", "other", "SKILL.md"), skill: true));
    Assert.Equal(OperatingSystem.IsWindows(), ignores.IsIgnored(Path.Combine(root, "SKILLS", "SKILL.md"), skill: true));
    Assert.False(ignores.IsIgnored(Path.Combine(skills, "AGENTS.md"), skill: false));
    Assert.True(ignores.IsIgnored(Path.Combine(root, "agent-instructions.MD"), skill: false));
    Assert.True(ignores.IsIgnored(Path.Combine(root, "agent_instruction.MD"), skill: false));
    Assert.False(ignores.IsIgnored(Path.Combine(root, "agent-instructions.MD"), skill: true));
    Assert.False(ignores.IsIgnored(Path.Combine(root, "CLAUDE.md"), skill: false));
  }

  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public void IncludesPersistAndMostSpecificPathWins(bool skill) {
    using var database = new TemporaryDatabase();
    string root = Path.GetDirectoryName(database.Path)!;
    string included = Path.Combine(root, "included");
    string excluded = Path.Combine(included, "excluded");
    using (UrdStore store = UrdStore.Open(database.Path)) {
      new PreferenceManager(store).SetSourceIgnores(new() {
        Instructions = [root, excluded], Skills = [root, excluded],
        InstructionIncludes = [included, included], SkillIncludes = [included], Filenames = ["CLAUDE.md"]
      });
    }
    using UrdStore reopened = UrdStore.Open(database.Path);
    SourceIgnoreRules rules = new PreferenceManager(reopened).Read().SourceIgnores;
    Assert.Equal(included, Assert.Single(rules.InstructionIncludes));
    Assert.Equal(included, Assert.Single(rules.SkillIncludes));
    Assert.True(rules.IsIgnored(Path.Combine(root, "sibling", "AGENTS.md"), skill));
    Assert.False(rules.IsIgnored(Path.Combine(included, "AGENTS.md"), skill));
    Assert.True(rules.IsIgnored(Path.Combine(excluded, "AGENTS.md"), skill));
    Assert.True(rules.IsIgnored(Path.Combine(root, "included-other", "AGENTS.md"), skill));
    Assert.Equal(!skill, rules.IsIgnored(Path.Combine(included, "CLAUDE.md"), skill));
    SourceIgnoreRules tied = rules with { Instructions = [included], Skills = [included] };
    Assert.False(tied.IsIgnored(Path.Combine(included, "AGENTS.md"), skill));
  }

  [Fact]
  public void OlderJsonWithoutIncludesStillLoads() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    database.Execute("INSERT INTO preferences (singleton, source_ignores_json) VALUES (1, '{\"Instructions\":[],\"Skills\":[],\"Filenames\":[]}');"); // raw-sql: seed pre-includes JSON
    SourceIgnoreRules rules = new PreferenceManager(store).Read().SourceIgnores;
    Assert.Empty(rules.InstructionIncludes);
    Assert.Empty(rules.SkillIncludes);
  }

  [Fact]
  public void RejectsInvalidRulesWithoutReplacingSavedPreferences() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    var preferences = new PreferenceManager(store);
    preferences.SetSourceIgnores(new() { Filenames = [] });

    Assert.Throws<ArgumentNullException>(() => preferences.SetSourceIgnores(null!));
    Assert.Throws<ArgumentNullException>(() => preferences.SetSourceIgnores(new() { Instructions = null! }));
    Assert.Throws<ArgumentNullException>(() => preferences.SetSourceIgnores(new() { Skills = null! }));
    Assert.Throws<ArgumentNullException>(() => preferences.SetSourceIgnores(new() { Filenames = null! }));
    Assert.Throws<ArgumentException>(() => preferences.SetSourceIgnores(new() { Instructions = ["relative"] }));
    Assert.Throws<ArgumentException>(() => preferences.SetSourceIgnores(new() { Skills = [null!] }));
    Assert.Throws<ArgumentException>(() => preferences.SetSourceIgnores(new() { InstructionIncludes = ["relative"] }));
    Assert.Throws<ArgumentException>(() => preferences.SetSourceIgnores(new() { SkillIncludes = [null!] }));
    Assert.Throws<ArgumentException>(() => preferences.SetSourceIgnores(new() { Filenames = [null!] }));
    Assert.Throws<ArgumentException>(() => preferences.SetSourceIgnores(new() { Filenames = ["notes/AGENTS.md"] }));
    Assert.Empty(preferences.Read().SourceIgnores.Filenames);
  }

  [Fact]
  public void JsonNullUsesDefaultsAndExplicitEmptyFilenamesSurviveReopening() {
    using var database = new TemporaryDatabase();
    using (UrdStore store = UrdStore.Open(database.Path)) {
      database.Execute("INSERT INTO preferences (singleton, source_ignores_json) VALUES (1, 'null');"); // raw-sql: seed JSON null separately from SQL NULL
      var preferences = new PreferenceManager(store);
      SourceIgnoreRules defaults = preferences.Read().SourceIgnores;
      Assert.Empty(defaults.Instructions);
      Assert.Empty(defaults.Skills);
      Assert.True(defaults.IsIgnored("claude.md", skill: false));
      Assert.True(defaults.IsIgnored("GEMINI.md", skill: false));
      Assert.False(defaults.IsIgnored("CLAUDE.md", skill: true));
      preferences.SetSourceIgnores(new() { Filenames = [] });
    }

    using UrdStore reopened = UrdStore.Open(database.Path);
    Assert.Empty(new PreferenceManager(reopened).Read().SourceIgnores.Filenames);
  }

  [Theory]
  [InlineData("{")]
  [InlineData("[]")]
  [InlineData("{\"Filenames\":null}")]
  [InlineData("{\"Instructions\":[null]}")]
  [InlineData("{\"Skills\":[\"relative\"]}")]
  [InlineData("{\"InstructionIncludes\":null}")]
  [InlineData("{\"SkillIncludes\":[\"relative\"]}")]
  [InlineData("{\"Filenames\":[\"*.md\"]}")]
  public void RejectsMalformedStoredPayload(string payload) {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    database.Execute($"INSERT INTO preferences (singleton, source_ignores_json) VALUES (1, '{payload}');"); // raw-sql: seed malformed stored JSON the manager will not write
    Assert.Throws<JsonException>(() => new PreferenceManager(store).Read());
  }
}
