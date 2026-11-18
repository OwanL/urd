namespace Urd.Core.Preferences;

public enum ModelBucket { Small, Medium, Large }
public enum AuxiliaryModelPurpose { Tools, Skills, Instructions, Title }
public enum ProtectedResourceCategory { Tools, Skills, Instructions }

// A user-configured provider/model pair used to fill a bucket or override one operation.
public sealed record ConfiguredModel(Guid ConnectionId, string ModelId) {
  internal void Validate() {
    if (ConnectionId == Guid.Empty || !ModelChoice.IsValidModelId(ModelId))
      throw new ArgumentException("Select a valid provider/model pair.");
  }
}

// Exactly one of Bucket or the provider/model pair is selected.
public sealed record ModelChoice(ModelBucket? Bucket, Guid? ConnectionId, string? ModelId) {
  public static ModelChoice ForBucket(ModelBucket bucket) => new(bucket, null, null);
  public static ModelChoice ForModel(Guid connectionId, string modelId) => new(null, connectionId, modelId);

  internal ConfiguredModel? ConfiguredModel() => ConnectionId is Guid connectionId && ModelId is not null
      ? new(connectionId, ModelId) : null;

  internal void Validate() {
    if (Bucket is ModelBucket bucket && ConnectionId is null && ModelId is null && Enum.IsDefined(bucket))
      return;

    if (Bucket is null && ConnectionId is Guid connectionId && connectionId != Guid.Empty
        && IsValidModelId(ModelId))
      return;

    throw new ArgumentException("Select one model bucket or a valid provider/model pair.");
  }

  internal static bool IsValidModelId(string? modelId) => !string.IsNullOrWhiteSpace(modelId)
      && modelId.Length <= 256 && !modelId.Any(char.IsControl);
}

// Buckets store explicit provider/model pairs; operation choices remain independent.
public sealed record AuxiliaryModelPreferences {
  public ModelChoice Tools { get; init; } = ModelChoice.ForBucket(ModelBucket.Small);
  public ModelChoice Skills { get; init; } = ModelChoice.ForBucket(ModelBucket.Small);
  public ModelChoice Instructions { get; init; } = ModelChoice.ForBucket(ModelBucket.Small);
  public ModelChoice Title { get; init; } = ModelChoice.ForBucket(ModelBucket.Small);
  public IReadOnlyList<ConfiguredModel> Small { get; init; } = [];
  public IReadOnlyList<ConfiguredModel> Medium { get; init; } = [];
  public IReadOnlyList<ConfiguredModel> Large { get; init; } = [];

  internal ModelChoice ChoiceFor(AuxiliaryModelPurpose purpose) => purpose switch {
    AuxiliaryModelPurpose.Tools => Tools,
    AuxiliaryModelPurpose.Skills => Skills,
    AuxiliaryModelPurpose.Instructions => Instructions,
    AuxiliaryModelPurpose.Title => Title,
    _ => throw new ArgumentOutOfRangeException(nameof(purpose))
  };

  internal IReadOnlyList<ConfiguredModel> ModelsFor(ModelBucket bucket) => bucket switch {
    ModelBucket.Small => Small,
    ModelBucket.Medium => Medium,
    ModelBucket.Large => Large,
    _ => throw new ArgumentOutOfRangeException(nameof(bucket))
  };

  internal AuxiliaryModelPreferences WithChoice(AuxiliaryModelPurpose purpose, ModelChoice choice) => purpose switch {
    AuxiliaryModelPurpose.Tools => this with { Tools = choice },
    AuxiliaryModelPurpose.Skills => this with { Skills = choice },
    AuxiliaryModelPurpose.Instructions => this with { Instructions = choice },
    AuxiliaryModelPurpose.Title => this with { Title = choice },
    _ => throw new ArgumentOutOfRangeException(nameof(purpose))
  };

  internal AuxiliaryModelPreferences WithBucketModels(ModelBucket bucket, IReadOnlyList<ConfiguredModel> models) => bucket switch {
    ModelBucket.Small => this with { Small = models },
    ModelBucket.Medium => this with { Medium = models },
    ModelBucket.Large => this with { Large = models },
    _ => throw new ArgumentOutOfRangeException(nameof(bucket))
  };
}

// Tool IDs and discovered resource source paths are retained as exact identities.
public sealed record ProtectedResourcePreferences {
  public IReadOnlyList<string> Tools { get; init; } = ["bash", "read", "write", "edit"];
  public IReadOnlyList<string> Skills { get; init; } = [];
  public IReadOnlyList<string> Instructions { get; init; } = [];

  internal IReadOnlyList<string> ForCategory(ProtectedResourceCategory category) => category switch {
    ProtectedResourceCategory.Tools => Tools,
    ProtectedResourceCategory.Skills => Skills,
    ProtectedResourceCategory.Instructions => Instructions,
    _ => throw new ArgumentOutOfRangeException(nameof(category))
  };

  internal ProtectedResourcePreferences WithCategory(
      ProtectedResourceCategory category, IReadOnlyList<string> items) => category switch {
    ProtectedResourceCategory.Tools => this with { Tools = items },
    ProtectedResourceCategory.Skills => this with { Skills = items },
    ProtectedResourceCategory.Instructions => this with { Instructions = items },
    _ => throw new ArgumentOutOfRangeException(nameof(category))
  };
}
