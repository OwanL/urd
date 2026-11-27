using OpenAI.Responses;
using Urd.Core.Execution;

#pragma warning disable OPENAI001 // Native Responses streaming types.

namespace Urd.Core.Providers;

// Display-only bookkeeping is bounded independently of the provider validation state.
internal sealed class ProviderToolDraftCapture(ToolDraftBuffer drafts) {
  private readonly Dictionary<int, string> responseItems = [];
  private readonly HashSet<int> chatIndexes = [];

  internal void ChatDelta(int index, string? id, string? name, ReadOnlyMemory<byte> delta) {
    if (index < 0 || (chatIndexes.Count >= ToolDraftBuffer.CallLimit && !chatIndexes.Contains(index)))
      return;
    chatIndexes.Add(index);
    drafts.AppendChat(index, id, name, delta.Span);
  }

  internal void ChatFinish() {
    foreach (int index in chatIndexes) drafts.Complete(index, null, null);
  }

  internal void Added(int index, ResponseItem item) => CaptureItem(index, item, complete: false);
  internal void ItemDone(int index, ResponseItem item) => CaptureItem(index, item, complete: true);

  private void CaptureItem(int index, ResponseItem item, bool complete) {
    if (index < 0 || item is not FunctionCallResponseItem call
        || call.Id is not { Length: > 0 and <= ToolDraftBuffer.IdentityLimit })
      return;
    if (!responseItems.TryGetValue(index, out string? itemId)) {
      if (responseItems.Count == ToolDraftBuffer.CallLimit)
        return;
      responseItems.Add(index, call.Id);
    } else if (itemId != call.Id) {
      return;
    }

    ReadOnlyMemory<byte> arguments = call.FunctionArguments?.ToMemory() ?? default;
    if (complete)
      drafts.CompleteItem(index, call.CallId, call.FunctionName, arguments.Span);
    else
      drafts.Append(index, call.CallId, call.FunctionName, arguments.Span);
  }

  internal void Delta(StreamingResponseFunctionCallArgumentsDeltaUpdate update) {
    if (Matches(update.OutputIndex, update.ItemId) && update.Delta is { } delta)
      drafts.Append(update.OutputIndex, null, null, delta.ToMemory().Span);
  }

  internal void ArgumentsDone(StreamingResponseFunctionCallArgumentsDoneUpdate update) {
    if (Matches(update.OutputIndex, update.ItemId)) {
      ReadOnlyMemory<byte> arguments = update.FunctionArguments?.ToMemory() ?? default;
      drafts.Complete(update.OutputIndex, null, update.FunctionName, arguments.Span);
    }
  }

  private bool Matches(int index, string? itemId) =>
      responseItems.TryGetValue(index, out string? knownId) && knownId == itemId;
}

#pragma warning restore OPENAI001
