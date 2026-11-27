using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.ML.Tokenizers;
using Urd.Core.Providers;

namespace Urd.Core.Usage;

public sealed class UsageTokenCounter {
  private static readonly HashSet<string> O200kModels = new(StringComparer.Ordinal) {
    "gpt-6-astra", "gpt-6.1-sol", "gpt-6.1-terra", "gpt-6-luna", "gpt-6-sol",
    "gpt-5.5", "gpt-5.4", "gpt-5.4-mini", "gpt-5.4-nano", "gpt-5.3-codex",
    "gpt-5.2", "gpt-5.1", "gpt-5", "gpt-5-mini", "gpt-5-nano",
    "gpt-4.1", "gpt-4.1-mini", "gpt-4o", "gpt-4o-mini", "o3"
  };
  private static readonly HashSet<string> contextEstimateModels = new(StringComparer.Ordinal) {
    "gpt-6.2-sol"
  };
  private readonly TiktokenTokenizer? tokenizer;
  private readonly object outputGate = new();
  private readonly StringBuilder output = new();
  private bool outputUnknown;
  private bool outputDirty;
  private long outputVersion;
  private long? cachedOutputTokens = 0;

  public UsageTokenCounter(string model) {
    ArgumentNullException.ThrowIfNull(model);
    if (UsesO200k(model)) tokenizer = O200kTokens.Instance;
  }

  internal static bool UsesO200k(string model) => O200kModels.Contains(model);

  internal static bool SupportsContextEstimate(string model) =>
      UsesO200k(model) || contextEstimateModels.Contains(model);

  public long? CountInput(IReadOnlyList<ChatMessage> messages, ChatOptions options) {
    ArgumentNullException.ThrowIfNull(messages);
    ArgumentNullException.ThrowIfNull(options);
    if (tokenizer is null) return null;

    long total = 0;
    if (!string.IsNullOrEmpty(options.Instructions)) {
      long? instructions = CountMessage("system", null, [options.Instructions]);
      if (instructions is not long count || !Add(ref total, count)) return null;
    }

    foreach (ChatMessage message in messages) {
      ArgumentNullException.ThrowIfNull(message);
      long? count = CountMessage(message);
      if (count is not long value || !Add(ref total, value)) return null;
    }

    if (options.Tools is not null) {
      foreach (AITool tool in options.Tools) {
        if (tool is not AIFunctionDeclaration function) return null;
        long? count = CountTool(function);
        if (count is not long value || !Add(ref total, value)) return null;
      }
    }
    return total;
  }

  public void Append(ChatResponseUpdate update) {
    ArgumentNullException.ThrowIfNull(update);
    lock (outputGate) {
      if (update.RawRepresentation is not null) outputUnknown = true;

      foreach (AIContent content in update.Contents) {
        if (content is UsageContent) continue;
        if (content.RawRepresentation is not null) {
          outputUnknown = true;
          continue;
        }

        if (content is TextContent text) AppendVisibleText(text.Text);
        else if (content is TextReasoningContent reasoning && reasoning.ProtectedData is null)
          AppendVisibleText(reasoning.Text);
        else outputUnknown = true;
      }
    }
  }

  private void AppendVisibleText(string text) {
    if (output.Length + (long)text.Length > 2 * 1024 * 1024) {
      outputUnknown = true;
      return;
    }
    output.Append(text);
    outputDirty = true;
    outputVersion++;
  }

  public long? OutputTokens {
    get {
      if (tokenizer is null) return null;
      string text;
      long version;
      lock (outputGate) {
        if (outputUnknown) return null;
        if (!outputDirty) return cachedOutputTokens;
        text = output.ToString();
        version = outputVersion;
        outputDirty = false;
      }

      long? count = CountText(text);
      lock (outputGate) {
        if (outputUnknown) return null;
        if (version == outputVersion) cachedOutputTokens = count;
        else outputDirty = true;
      }
      return count;
    }
  }

  // Wire framing differs by model; count role, author and content together plus a fixed framing estimate.
  private long? CountMessage(ChatMessage message) {
    if (message.RawRepresentation is not null) return null;
    var text = new StringBuilder();
    text.Append(message.Role).Append('\n').Append(message.AuthorName).Append('\n');
    foreach (AIContent content in message.Contents) {
      string? part = ContentText(content);
      if (part is null) return null;
      text.Append(part).Append('\n');
    }
    return CountFramed(text.ToString(), 4);
  }

  private long? CountMessage(string role, string? author, IEnumerable<string> contents) {
    var text = new StringBuilder();
    text.Append(role).Append('\n').Append(author).Append('\n');
    foreach (string content in contents) text.Append(content).Append('\n');
    return CountFramed(text.ToString(), 4);
  }

  private long? CountTool(AIFunctionDeclaration function) {
    string text = $"tool\n{function.Name}\n{function.Description}\n{function.JsonSchema.GetRawText()}";
    return CountFramed(text, 8);
  }

  private long? CountFramed(string text, int framingTokens) {
    long? count = CountText(text);
    return count is long value && value <= long.MaxValue - framingTokens ? value + framingTokens : null;
  }

  private long? CountText(string text) {
    try {
      return tokenizer!.CountTokens(text);
    } catch (ArgumentException) {
      return null;
    } catch (InvalidOperationException) {
      return null;
    }
  }

  private static string? ContentText(AIContent content) {
    if (content.RawRepresentation is not null) return null;
    return content switch {
      TextContent text => text.Text,
      TextReasoningContent reasoning when reasoning.ProtectedData is null => reasoning.Text,
      FunctionCallContent call => FunctionCallText(call),
      FunctionResultContent result => FunctionResultText(result.Result),
      _ => null
    };
  }

  private static string? FunctionCallText(FunctionCallContent call) {
    try {
      return $"{call.Name}\n{JsonSerializer.Serialize(call.Arguments)}";
    } catch (Exception exception) when (exception is NotSupportedException or JsonException or InvalidOperationException) {
      return null;
    }
  }

  private static string? FunctionResultText(object? result) {
    if (result is null) return string.Empty;
    if (result is string text) return text;
    if (result is JsonElement json) return json.GetRawText();
    if (result is AIContent content) return ContentText(content);
    if (result is IEnumerable<AIContent> contents) {
      var textResult = new StringBuilder();
      foreach (AIContent item in contents) {
        string? part = ContentText(item);
        if (part is null) return null;
        textResult.Append(part).Append('\n');
      }
      return textResult.ToString();
    }

    try {
      return JsonSerializer.Serialize(result);
    } catch (Exception exception) when (exception is NotSupportedException or JsonException or InvalidOperationException) {
      return null;
    }
  }

  private static bool Add(ref long total, long value) {
    if (value < 0 || total > long.MaxValue - value) return false;
    total += value;
    return true;
  }
}
