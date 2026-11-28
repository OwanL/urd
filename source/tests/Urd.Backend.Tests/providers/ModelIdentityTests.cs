using Urd.Core.Providers;
using Xunit;

namespace Urd.Backend.Tests.Providers;

public sealed class ModelIdentityTests {
  [Theory]
  [InlineData("gpt-5.2-codex", null, null, ModelFamily.OpenAI)]
  [InlineData("o4-mini", null, null, ModelFamily.OpenAI)]
  [InlineData("vendor/claude-sonnet-4.5", null, null, ModelFamily.Anthropic)]
  [InlineData("unknown", "Gemini 3 Pro", null, ModelFamily.Google)]
  [InlineData("opaque", "Opaque", "Anthropic", ModelFamily.Anthropic)]
  [InlineData("opaque", "Opaque", "unrecognized", ModelFamily.Other)]
  public void InfersKnownFamiliesWithoutGuessingUnknownModels(
      string id, string? name, string? vendor, ModelFamily expected) =>
      Assert.Equal(expected, ModelIdentity.Infer(id, name, vendor));

  [Fact]
  public void OfficialProviderRouteAndCatalogMetadataTakePrecedence() {
    var openAI = new ProviderConnection(Guid.NewGuid(), "OpenAI", ProviderRoute.OpenAI,
        LlmApi.OpenAIResponses, new Uri("https://api.openai.com/v1/"), AuthenticationMethod.ApiKey);
    var custom = openAI with { Id = Guid.NewGuid(), Route = ProviderRoute.Custom };

    Assert.Equal(ModelFamily.OpenAI, ModelIdentity.Resolve(openAI, new("opaque", "Opaque")));
    Assert.Equal(ModelFamily.Google,
        ModelIdentity.Resolve(custom, new("gpt-looking", "GPT looking", Family: ModelFamily.Google)));
  }
}
