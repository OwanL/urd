using System.Text.Json;
using System.Text.Json.Serialization;
using Urd.Core.Preferences;

namespace Urd.Core.Storage;

// Stores and caches the shared application preferences.
internal sealed class PreferencesStorage(Database database) {
  private static readonly JsonSerializerOptions JsonOptions = new() {
    Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    RespectRequiredConstructorParameters = true,
    RespectNullableAnnotations = true
  };
  private readonly object gate = new();
  private UserPreferences? cachedPreferences;

  internal UserPreferences Read() {
    lock (gate)
      return Copy(ReadLocked());
  }

  // Keep validation and its storage transaction serialized with preference updates.
  internal T WithCurrent<T>(Func<UserPreferences, T> operation) {
    lock (gate)
      return operation(ReadLocked());
  }

  internal UserPreferences Update(Func<UserPreferences, UserPreferences> update) {
    ArgumentNullException.ThrowIfNull(update);
    lock (gate) {
      UserPreferences current = ReadLocked();
      UserPreferences updated = update(current);
      if (ReferenceEquals(current, updated))
        return Copy(current);
      database.Write(transaction => {
        database.Execute(transaction, """
          INSERT INTO preferences (singleton, working_directory, compaction_trigger_token_count,
              model_choices_json, protected_resources_json, subagent_providers_json, lan_hosting_enabled, source_ignores_json,
              max_subagent_depth)
          VALUES (1, $directory, $compactionTriggerTokenCount, $modelChoices, $protectedResources, $subagentProviders,
              $lanHostingEnabled, $sourceIgnores, $maxSubagentDepth)
          ON CONFLICT(singleton) DO UPDATE SET
              working_directory = excluded.working_directory,
              compaction_trigger_token_count = excluded.compaction_trigger_token_count,
              model_choices_json = excluded.model_choices_json,
              protected_resources_json = excluded.protected_resources_json,
              subagent_providers_json = excluded.subagent_providers_json,
              lan_hosting_enabled = excluded.lan_hosting_enabled,
              source_ignores_json = excluded.source_ignores_json,
              max_subagent_depth = excluded.max_subagent_depth;
          """, ("$directory", updated.WorkingDirectory),
            ("$compactionTriggerTokenCount", updated.CompactionTriggerTokenCount),
            ("$modelChoices", JsonSerializer.Serialize(updated.ModelChoices, JsonOptions)),
            ("$protectedResources", JsonSerializer.Serialize(updated.ProtectedResources, JsonOptions)),
            ("$subagentProviders", updated.SubagentProviderConnectionIds is null
                ? null : JsonSerializer.Serialize(updated.SubagentProviderConnectionIds, JsonOptions)),
            ("$lanHostingEnabled", updated.LanHostingEnabled),
            ("$sourceIgnores", JsonSerializer.Serialize(updated.SourceIgnores, JsonOptions)),
            ("$maxSubagentDepth", updated.MaxSubagentDepth));
        return true;
      });
      cachedPreferences = updated;
      return Copy(updated);
    }
  }

  private UserPreferences ReadLocked() {
    if (cachedPreferences is not null)
      return cachedPreferences;

    UserPreferences persisted = database.Read(transaction => database.QuerySingleOrDefault(transaction, """
        SELECT working_directory, compaction_trigger_token_count, model_choices_json, protected_resources_json,
            subagent_providers_json, lan_hosting_enabled, source_ignores_json, max_subagent_depth
        FROM preferences WHERE singleton = 1;
        """, row => new UserPreferences(row.NullableString("working_directory"),
            row.NullableInt("compaction_trigger_token_count")) {
          LanHostingEnabled = row.Bool("lan_hosting_enabled"),
          MaxSubagentDepth = row.Int("max_subagent_depth"),
          ModelChoices = Deserialize<AuxiliaryModelPreferences>(row.NullableString("model_choices_json")) ?? new(),
          ProtectedResources = Deserialize<ProtectedResourcePreferences>(row.NullableString("protected_resources_json")) ?? new(),
          SubagentProviderConnectionIds = Deserialize<Guid[]>(row.NullableString("subagent_providers_json")),
          SourceIgnores = DeserializeSourceIgnores(row.NullableString("source_ignores_json"))
        }) ?? new UserPreferences(null, null));
    cachedPreferences = persisted;
    return persisted;
  }

  private static T? Deserialize<T>(string? json) =>
      json is null ? default : JsonSerializer.Deserialize<T>(json, JsonOptions);

  private static SourceIgnoreRules DeserializeSourceIgnores(string? json) {
    SourceIgnoreRules? rules = Deserialize<SourceIgnoreRules>(json);
    try {
      return rules?.Normalize() ?? new();
    } catch (ArgumentException failure) {
      throw new JsonException("Stored source ignore rules are invalid.", failure);
    }
  }

  private static UserPreferences Copy(UserPreferences preferences) => preferences with {
    SourceIgnores = preferences.SourceIgnores with {
      Instructions = preferences.SourceIgnores.Instructions.ToArray(),
      Skills = preferences.SourceIgnores.Skills.ToArray(),
      InstructionIncludes = preferences.SourceIgnores.InstructionIncludes.ToArray(),
      SkillIncludes = preferences.SourceIgnores.SkillIncludes.ToArray(),
      Filenames = preferences.SourceIgnores.Filenames.ToArray()
    },
    ModelChoices = preferences.ModelChoices with {
      Small = preferences.ModelChoices.Small.ToArray(),
      Medium = preferences.ModelChoices.Medium.ToArray(),
      Large = preferences.ModelChoices.Large.ToArray()
    },
    SubagentProviderConnectionIds = preferences.SubagentProviderConnectionIds?.ToArray(),
    ProtectedResources = preferences.ProtectedResources with {
      Tools = preferences.ProtectedResources.Tools.ToArray(),
      Skills = preferences.ProtectedResources.Skills.ToArray(),
      Instructions = preferences.ProtectedResources.Instructions.ToArray()
    }
  };
}
