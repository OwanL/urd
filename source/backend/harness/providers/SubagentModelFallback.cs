using Urd.Core.Preferences;

namespace Urd.Core.Providers;

internal static class SubagentModelFallback {
  internal static ProviderModel? FindLatest(ModelBucket bucket, ProviderModelCatalog catalog) {
    string suffix = bucket switch {
      ModelBucket.Small => "-luna",
      ModelBucket.Medium => "-terra",
      ModelBucket.Large => "-sol",
      _ => throw new ArgumentOutOfRangeException(nameof(bucket))
    };

    ProviderModel? best = null;
    ReadOnlySpan<char> bestVersion = default;
    foreach (ProviderModel model in catalog.Models) {
      ReadOnlySpan<char> version = ReadVersion(model.Id, suffix);
      if (version.IsEmpty)
        continue;

      int order = CompareVersions(version, bestVersion);
      if (best is null || order > 0 || (order == 0 && string.CompareOrdinal(model.Id, best.Id) < 0)) {
        best = model;
        bestVersion = version;
      }
    }
    return best;
  }

  private static ReadOnlySpan<char> ReadVersion(string modelId, string suffix) {
    const string Prefix = "gpt-";
    if (modelId.Length <= Prefix.Length + suffix.Length || !modelId.StartsWith(Prefix, StringComparison.Ordinal)
        || !modelId.EndsWith(suffix, StringComparison.Ordinal))
      return default;

    ReadOnlySpan<char> version = modelId.AsSpan(Prefix.Length, modelId.Length - Prefix.Length - suffix.Length);
    bool needsDigit = true;
    foreach (char character in version) {
      if (char.IsAsciiDigit(character))
        needsDigit = false;
      else if (character == '.' && !needsDigit)
        needsDigit = true;
      else
        return default;
    }
    return needsDigit ? default : version;
  }

  // Missing components are zero. Compare digits directly so any component length is supported.
  private static int CompareVersions(ReadOnlySpan<char> left, ReadOnlySpan<char> right) {
    while (!left.IsEmpty || !right.IsEmpty) {
      ReadOnlySpan<char> leftDigits = TakeComponent(ref left).TrimStart('0');
      ReadOnlySpan<char> rightDigits = TakeComponent(ref right).TrimStart('0');
      int lengthOrder = leftDigits.Length.CompareTo(rightDigits.Length);
      if (lengthOrder != 0)
        return lengthOrder;
      int digitOrder = leftDigits.SequenceCompareTo(rightDigits);
      if (digitOrder != 0)
        return digitOrder;
    }
    return 0;
  }

  private static ReadOnlySpan<char> TakeComponent(ref ReadOnlySpan<char> version) {
    int separator = version.IndexOf('.');
    if (separator < 0) {
      ReadOnlySpan<char> component = version;
      version = default;
      return component;
    }

    ReadOnlySpan<char> firstComponent = version[..separator];
    version = version[(separator + 1)..];
    return firstComponent;
  }
}
