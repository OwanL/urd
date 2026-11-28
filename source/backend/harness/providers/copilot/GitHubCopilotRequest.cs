using System.ClientModel.Primitives;
using Microsoft.Extensions.AI;

namespace Urd.Core.Providers;

// Adds Copilot request metadata without identifying Urd as a different client.
internal sealed class GitHubCopilotRequest(IReadOnlyList<ChatMessage> messages) : PipelinePolicy {
  internal static void ApplyHeaders(HttpRequestMessage request, IReadOnlyList<ChatMessage> messages) =>
      ApplyHeaders((key, value) => {
        request.Headers.Remove(key);
        request.Headers.Add(key, value);
      }, messages);

  public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex) {
    ApplyHeaders(message.Request.Headers.Set, messages);
    ProcessNext(message, pipeline, currentIndex);
  }

  public override ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex) {
    ApplyHeaders(message.Request.Headers.Set, messages);
    return ProcessNextAsync(message, pipeline, currentIndex);
  }

  private static void ApplyHeaders(Action<string, string> set, IReadOnlyList<ChatMessage> input) {
    set("User-Agent", "Urd");
    set("X-GitHub-Api-Version", GitHubCopilotModels.ApiVersion);
    set("X-Initiator", input.Count == 0 || input[^1].Role == ChatRole.User ? "user" : "agent");
    set("Openai-Intent", "conversation-edits");
    if (input.Any(message => message.Contents.Any(ContainsImage)))
      set("Copilot-Vision-Request", "true");
  }

  private static bool ContainsImage(AIContent content) => content switch {
    DataContent data => data.HasTopLevelMediaType("image"),
    FunctionResultContent { Result: IReadOnlyList<AIContent> parts } => parts.OfType<DataContent>()
        .Any(part => part.HasTopLevelMediaType("image")),
    _ => false
  };
}
