using System.Text.Json;
using Microsoft.Extensions.AI;
using Urd.Core.Usage;

namespace Urd.Backend.Tests.Support;

// Keeps purpose-unaware test clients focused on main model calls while giving prepass work valid answers.
internal sealed class PrepassTolerantChatClient(IChatClient inner, bool disposeInner = true) : IChatClient {
  private int disposed;

  internal static IChatClient WrapIfNeeded(IChatClient client, bool disposeInner = true) =>
      client is ScriptedChatClient { IsPurposeAware: true } ? client : new PrepassTolerantChatClient(client, disposeInner);

  public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
      ChatOptions? options = null, CancellationToken cancellationToken = default) {
    string? answer = Answer(Purpose(options), messages);
    return answer is null ? inner.GetStreamingResponseAsync(messages, options, cancellationToken)
        : ScriptedResponses.TextUpdates([answer], cancellationToken);
  }

  public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
      CancellationToken cancellationToken = default) {
    string? answer = Answer(Purpose(options), messages);
    return answer is null ? inner.GetResponseAsync(messages, options, cancellationToken)
        : Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, answer)) {
          FinishReason = ChatFinishReason.Stop
        });
  }

  public object? GetService(Type serviceType, object? serviceKey = null) =>
      serviceKey is null && serviceType.IsInstanceOfType(this) ? this : inner.GetService(serviceType, serviceKey);

  public void Dispose() {
    if (Interlocked.Exchange(ref disposed, 1) == 0 && disposeInner)
      inner.Dispose();
  }

  private static string? Purpose(ChatOptions? options) => options?.AdditionalProperties?
      .TryGetValue(UsageContext.PropertyName, out object? value) == true && value is UsageContext usage
      ? usage.Purpose : null;

  private static string? Answer(string? purpose, IEnumerable<ChatMessage> messages) => purpose switch {
    "title" => "Test session",
    "pruning.tools" or "pruning.skills" or "pruning.instructions" => KeepAll(messages),
    _ => null
  };

  internal static string KeepAll(IEnumerable<ChatMessage> messages) {
    string prompt = messages.Last(message => message.Role == ChatRole.User).Text;
    const string marker = "Candidates:\n";
    int start = prompt.LastIndexOf(marker, StringComparison.Ordinal);
    using JsonDocument candidates = JsonDocument.Parse(prompt[(start + marker.Length)..]);
    string[] ids = candidates.RootElement.EnumerateArray().SelectMany(candidate => {
      if (candidate.TryGetProperty("Files", out JsonElement files))
        return files.EnumerateArray().Select(file => file.GetProperty("Id").GetString()!);
      JsonElement value = candidate.TryGetProperty("Id", out JsonElement id) ? id : candidate.GetProperty("id");
      return new[] { value.GetString()! };
    }).ToArray();
    return JsonSerializer.Serialize(new { keptIds = ids });
  }
}
