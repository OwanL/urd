using System.Text.Json;
using Urd.Core.Providers;
using Xunit;

namespace Urd.Backend.Tests.Providers;

public sealed class ModelContextWindowsTests {
  [Fact]
  public void DefaultsRequireTrustedRouteAndExactModelId() {
    var official = Connection(ProviderRoute.Custom, "https://api.openai.com/v1/");
    var custom = Connection(ProviderRoute.Custom, "https://fixture.invalid/v1/");
    var lookalike = Connection(ProviderRoute.Custom, "https://api.openai.com.evil.invalid/v1/");
    var wrongPath = Connection(ProviderRoute.Custom, "https://api.openai.com/v1beta/");
    var insecure = Connection(ProviderRoute.Custom, "http://api.openai.com/v1/");
    var chatGpt = Connection(ProviderRoute.ChatGpt, "https://chatgpt.com/backend-api");

    Assert.Equal(128_000, ModelContextWindows.DefaultFor(official, "gpt-4o"));
    Assert.Null(ModelContextWindows.DefaultFor(custom, "gpt-4o"));
    Assert.Null(ModelContextWindows.DefaultFor(lookalike, "gpt-4o"));
    Assert.Null(ModelContextWindows.DefaultFor(wrongPath, "gpt-4o"));
    Assert.Null(ModelContextWindows.DefaultFor(insecure, "gpt-4o"));
    Assert.Null(ModelContextWindows.DefaultFor(official, "GPT-4o"));
    Assert.Null(ModelContextWindows.DefaultFor(official, "gpt-4o-custom"));
    Assert.Equal(272_000, ModelContextWindows.DefaultFor(chatGpt, "gpt-5.4-mini"));
    Assert.Null(ModelContextWindows.DefaultFor(chatGpt, "gpt-4o"));
  }

  [Fact]
  public void PositiveReportedWindowWinsAndInvalidValuesFallBack() {
    var official = Connection(ProviderRoute.OpenAI, "https://api.openai.com/v1/");
    using JsonDocument positive = JsonDocument.Parse("{\"context_window\":4096}");
    using JsonDocument invalid = JsonDocument.Parse("{\"context_window\":0}");
    using JsonDocument missing = JsonDocument.Parse("{}");

    Assert.Equal(4096, ModelContextWindows.Resolve(official, "gpt-4o", positive.RootElement));
    Assert.Equal(128_000, ModelContextWindows.Resolve(official, "gpt-4o", invalid.RootElement));
    Assert.Equal(128_000, ModelContextWindows.Resolve(official, "gpt-4o", missing.RootElement));
    Assert.Equal(4096, ModelContextWindows.Resolve(Connection(ProviderRoute.Custom, "https://fixture.invalid/v1/"),
        "unlisted-model", positive.RootElement));
  }

  private static ProviderConnection Connection(ProviderRoute route, string endpoint) =>
      new(Guid.NewGuid(), "Fixture", route, LlmApi.OpenAIResponses, new Uri(endpoint), AuthenticationMethod.None);
}
