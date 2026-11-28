using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Urd.Core.Preferences;

namespace Urd.Core.Providers;

// Discovers the account's selectable models and their advertised HTTP transports.
internal sealed class GitHubCopilotModels(HttpClient client) {
  internal const string ApiVersion = "2026-06-01";
  private const int MaximumCatalogBytes = 2 * 1024 * 1024;

  internal async Task<IReadOnlyList<ProviderModel>> GetModelsAsync(ProviderCredentials credentials,
      CancellationToken token) {
    using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(GitHubCopilotAuthentication.GetEndpoint(credentials), "models"));
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.AccessToken);
    request.Headers.Accept.ParseAdd("application/json");
    GitHubCopilotRequest.ApplyIdentityHeaders(request);
    request.Headers.Add("X-GitHub-Api-Version", ApiVersion);
    try {
      using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
      if (!response.IsSuccessStatusCode)
        throw new ProviderOperationException(response.StatusCode switch {
          HttpStatusCode.Unauthorized => ProviderFailure.AuthenticationRequired,
          HttpStatusCode.Forbidden => ProviderFailure.PlanAccessDenied,
          HttpStatusCode.NotFound => ProviderFailure.DiscoveryUnsupported,
          HttpStatusCode.TooManyRequests => ProviderFailure.RateLimited,
          _ => ProviderFailure.ProviderUnavailable
        });
      await response.Content.LoadIntoBufferAsync(MaximumCatalogBytes, token);
      await using Stream stream = await response.Content.ReadAsStreamAsync(token);
      using JsonDocument catalog = await JsonDocument.ParseAsync(stream, cancellationToken: token);
      return Parse(catalog.RootElement);
    } catch (JsonException) {
      throw new ProviderOperationException(ProviderFailure.InvalidProviderResponse);
    } catch (Exception error) when (error is HttpRequestException or IOException) {
      throw new ProviderOperationException(ProviderFailure.ProviderUnavailable);
    }
  }

  internal static IReadOnlyList<ProviderModel> Parse(JsonElement catalog) {
    JsonElement data = Property(catalog, "data");
    if (data.ValueKind != JsonValueKind.Array)
      throw new ProviderOperationException(ProviderFailure.InvalidProviderResponse);

    var models = new List<ProviderModel>();
    var modelIds = new HashSet<string>(StringComparer.Ordinal);
    foreach (JsonElement item in data.EnumerateArray()) {
      if (Property(item, "model_picker_enabled").ValueKind != JsonValueKind.True ||
          Text(Property(item, "policy"), "state") == "disabled")
        continue;
      if (Text(item, "id") is not { } id || !ModelChoice.IsValidModelId(id) || SelectApi(item) is not { } api)
        continue;
      if (!modelIds.Add(id))
        throw new ProviderOperationException(ProviderFailure.InvalidProviderResponse);

      string? name = Text(item, "name");
      string displayName = string.IsNullOrWhiteSpace(name) || name.Length > 256 || name.Any(char.IsControl) ? id : name;
      JsonElement capability = Property(item, "capabilities");
      JsonElement limits = Property(capability, "limits");
      JsonElement prices = Property(Property(Property(item, "billing"), "token_prices"), "default");
      int? defaultContext = PositiveInteger(prices, "max_prompt_tokens") ?? PositiveInteger(prices, "context_max");
      int? maximumContext = PositiveInteger(limits, "max_context_window_tokens");
      int? context = (defaultContext, maximumContext) switch {
        (int regular, int maximum) => Math.Min(regular, maximum),
        _ => defaultContext ?? maximumContext ?? PositiveInteger(limits, "max_prompt_tokens")
      };
      string[]? efforts = Strings(Property(Property(capability, "supports"), "reasoning_effort"));
      models.Add(new(id, displayName, context, api, PositiveInteger(limits, "max_output_tokens"), efforts,
          Family: ModelIdentity.Infer(id, displayName, Text(item, "vendor"))));
    }
    return models;
  }

  private static LlmApi? SelectApi(JsonElement model) {
    string[]? endpoints = Strings(Property(model, "supported_endpoints"));
    if (endpoints is null)
      return null;
    if (string.Equals(Text(model, "vendor"), "Anthropic", StringComparison.OrdinalIgnoreCase) && endpoints.Contains("/v1/messages"))
      return LlmApi.AnthropicMessages;
    if (endpoints.Contains("/responses"))
      return LlmApi.OpenAIResponses;
    if (endpoints.Contains("/chat/completions"))
      return LlmApi.OpenAIChatCompletions;
    return endpoints.Contains("/v1/messages") ? LlmApi.AnthropicMessages : null;
  }

  private static JsonElement Property(JsonElement value, string name) =>
      value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out JsonElement property) ? property : default;

  private static string? Text(JsonElement value, string name) => Property(value, name) is { ValueKind: JsonValueKind.String } text
      ? text.GetString() : null;

  private static int? PositiveInteger(JsonElement value, string name) =>
      Property(value, name) is { ValueKind: JsonValueKind.Number } number && number.TryGetInt32(out int result) && result > 0 ? result : null;

  private static string[]? Strings(JsonElement value) => value.ValueKind == JsonValueKind.Array
      ? value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!).ToArray() : null;
}
