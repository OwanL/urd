using Urd.Core.Execution;
using Urd.Core.Providers;

namespace Urd.Backend.Tests.Support;

// A stable, secret-free binding for tests that supply their own chat client.
internal static class TestLlmSelection {
  internal static LlmSelection Default { get; } = Create(new(
      Guid.Parse("00000000-0000-0000-0000-000000000001"), "test provider", ProviderRoute.Custom,
      LlmApi.OpenAIResponses, new Uri("https://fixture.invalid/v1/"), AuthenticationMethod.None));

  // The configured auxiliary path records the chat-selection stand-in as false.
  internal static AuxiliaryModelResolution DefaultResolution { get; } = new(Default, false);

  internal static AuxiliaryModelResolution UsedSessionResolution { get; } = new(Default, true);

  internal static LlmSelection Create(ProviderConnection connection, string modelId = "fixture-model")
      => new(connection, modelId, null, ModelFamily.Other, false);
}
