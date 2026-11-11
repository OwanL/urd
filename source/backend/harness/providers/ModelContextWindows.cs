using System.Text.Json;

namespace Urd.Core.Providers;

// Exact-ID fallbacks grounded in the pinned Pi OpenAI and OpenAI Codex model registries.
internal static class ModelContextWindows {
  private static readonly Uri OpenAIEndpoint = new("https://api.openai.com/v1/");
  private static readonly Dictionary<string, int> OpenAIDefaults = new Dictionary<string, int>(StringComparer.Ordinal) {
    ["gpt-4"] = 8_192,
    ["gpt-4-turbo"] = 128_000,
    ["gpt-4o"] = 128_000,
    ["gpt-4o-mini"] = 128_000,
    ["gpt-4.1"] = 1_047_576,
    ["gpt-4.1-mini"] = 1_047_576,
    ["gpt-4.1-nano"] = 1_047_576,
    ["gpt-5"] = 400_000,
    ["gpt-5-mini"] = 400_000,
    ["gpt-5.1"] = 400_000,
    ["gpt-5.2"] = 400_000,
    ["gpt-5.3-codex"] = 400_000,
    ["gpt-5.4"] = 272_000,
    ["gpt-5.4-mini"] = 400_000,
    ["gpt-5.5"] = 272_000,
    ["o1"] = 200_000,
    ["o1-pro"] = 200_000,
    ["o3"] = 200_000,
    ["o3-mini"] = 200_000,
    ["o4-mini"] = 200_000
  };
  private static readonly Dictionary<string, int> ChatGptDefaults = new Dictionary<string, int>(StringComparer.Ordinal) {
    ["gpt-5.3-codex-spark"] = 128_000,
    ["gpt-5.4"] = 272_000,
    ["gpt-5.4-mini"] = 272_000,
    ["gpt-5.5"] = 272_000,
    ["gpt-5.6-luna"] = 372_000,
    ["gpt-5.6-sol"] = 372_000,
    ["gpt-5.6-terra"] = 372_000
  };

  internal static int? Resolve(ProviderConnection connection, string modelId, JsonElement model) =>
      Resolve(connection, modelId, ReadReported(model));

  internal static int? Resolve(ProviderConnection connection, string modelId, int? reportedContextWindow) =>
      reportedContextWindow is > 0 ? reportedContextWindow : DefaultFor(connection, modelId);

  internal static int? DefaultFor(ProviderConnection connection, string modelId) {
    if (connection.Route == ProviderRoute.ChatGpt)
      return ChatGptDefaults.TryGetValue(modelId, out int chatGptWindow) ? chatGptWindow : null;

    if (!IsOfficialOpenAIEndpoint(connection.Endpoint))
      return null;

    return OpenAIDefaults.TryGetValue(modelId, out int openAIWindow) ? openAIWindow : null;
  }

  internal static int? ReadReported(JsonElement model) =>
      model.TryGetProperty("context_window", out JsonElement contextWindow) &&
      contextWindow.ValueKind == JsonValueKind.Number && contextWindow.TryGetInt32(out int value) && value > 0
          ? value : null;

  internal static void Validate(int? contextWindow, string parameterName) {
    if (contextWindow is <= 0)
      throw new ArgumentOutOfRangeException(parameterName, "A context window must be positive when specified.");
  }

  private static bool IsOfficialOpenAIEndpoint(Uri endpoint) =>
      endpoint is { IsAbsoluteUri: true, Scheme: "https" } && endpoint.Equals(OpenAIEndpoint);
}
