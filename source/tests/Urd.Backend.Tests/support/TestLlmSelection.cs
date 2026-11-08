using Urd.Core.Providers;

namespace Urd.Backend.Tests.Support;

// A stable, secret-free binding for tests that supply their own chat client.
internal static class TestLlmSelection {
  internal static LlmSelection Default { get; } = Create(new(
      Guid.Parse("00000000-0000-0000-0000-000000000001"), "test provider", ProviderRoute.Custom,
      LlmApi.OpenAIResponses, new Uri("https://fixture.invalid/v1/"), AuthenticationMethod.None));

  internal static LlmSelection Create(ProviderConnection connection, string modelId = "fixture-model")
      => new(connection, modelId, null);
}
