using System.Net;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Urd.Backend.Tests.Support;
using Urd.Core.Providers;
using Urd.Core.Storage;
using Xunit;
using static Urd.Backend.Tests.Support.TestAsync;

namespace Urd.Backend.Tests.Providers;

public sealed class ModelImageSupportTests {
  [Theory]
  [InlineData(true, false, "This model does not support image inputs.", true)]
  [InlineData(true, true, "This model does not support image inputs.", false)]
  [InlineData(false, false, "This model does not support image inputs.", false)]
  [InlineData(true, false, "Unsupported image format.", false)]
  public async Task OnlyExplicitModelRejectionOfAnImageSetsAnUnconfiguredModelOff(
      bool hasImage, bool savedOverride, string message, bool learn) {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    string errorBody = JsonSerializer.Serialize(new { error = new { message, code = "invalid_value" } });
    using var handler = new ScriptedOpenAIHandler(errorBody, "application/json", HttpStatusCode.BadRequest);
    var manager = new ProviderManager(store, () => new Uri("http://localhost:54321/"), handler);
    try {
      ProviderConnection connection = await manager.AddCustomAsync(new("Fixture", LlmApi.OpenAIResponses,
          new Uri("https://fixture.invalid/v1"), AuthenticationMethod.None));
      const string model = "fixture-model";
      if (savedOverride)
        await manager.SaveModelSettingsAsync(new(connection.Id, model, ModelFamily.OpenAI, true, 16_000));
      using IChatClient client = manager.CreateChatClient(connection);
      var input = new ChatMessage(ChatRole.User, "hello");
      if (hasImage)
        input.Contents.Add(new DataContent(new byte[] { 1, 2, 3 }, "image/png"));

      Task<ChatResponse> Send() => client.GetResponseAsync([input], new() { ModelId = model }).WaitAsync(TestTimeout);
      await Assert.ThrowsAsync<ProviderOperationException>(Send);
      Assert.Single(handler.Requests);
      Assert.Equal(hasImage, handler.Requests[0].Body.Contains("input_image", StringComparison.Ordinal));
      IReadOnlyList<ModelSettings> settings = await manager.GetModelSettingsAsync(connection.Id);
      if (learn) {
        var learned = Assert.Single(settings);
        Assert.Equal(model, learned.ModelId);
        Assert.False(learned.SupportsImages);
        var repeated = await Assert.ThrowsAsync<ProviderOperationException>(Send);
        Assert.Contains("disabled", repeated.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(handler.Requests);
      } else if (savedOverride) {
        Assert.Equal(new(connection.Id, model, ModelFamily.OpenAI, true, 16_000), Assert.Single(settings));
      } else {
        Assert.Empty(settings);
      }
    } finally {
      await manager.ShutdownAsync().WaitAsync(TestTimeout);
      manager.Dispose();
    }
  }
}
