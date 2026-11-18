using System.Buffers;
using System.Text;
using Urd.Core.Execution;
using Urd.Core.Storage;

namespace Urd.Core.MarkdownRendering;

// Observes only live, saved assistant text. The render and read paths never import a file.
internal sealed class MarkdownImageImports(MarkdownImageStorage storage) : IDisposable {
  private const int MaximumSourceChars = 512 * 1024;
  private const int MaximumTargetsPerResponse = 64;
  private const int MaximumPendingResponses = 64;
  private static readonly TimeSpan CoalesceInterval = TimeSpan.FromMilliseconds(100);
  private static readonly SearchValues<char> PossibleImageEnds = SearchValues.Create(")]:\r\n");
  private readonly object gate = new();
  private readonly SemaphoreSlim workerSlots = new(2);
  private readonly Dictionary<(Guid SessionId, Guid ExecutionId, Guid ResponseId), Response> pending = [];

  internal Observer ObserveExecution(ExecutionIdentity execution, string? workingDirectory) =>
      new(this, execution, workingDirectory);

  internal Task WaitForPendingAsync(Guid sessionId, Guid executionId, Guid responseId, CancellationToken cancellation) {
    Task work;
    lock (gate) {
      Response? response = pending.GetValueOrDefault((sessionId, executionId, responseId));
      work = (response?.QueuedBatch ?? response?.RunningBatch)?.Task ?? Task.CompletedTask;
    }
    return work.WaitAsync(cancellation);
  }

  // The store disposes after its owning sessions have joined image work.
  public void Dispose() => workerSlots.Dispose();

  private Task JoinExecutionAsync(ExecutionIdentity execution) {
    lock (gate)
      return Task.WhenAll(pending.Where(item => item.Key.SessionId == execution.SessionId
          && item.Key.ExecutionId == execution.ExecutionId).Select(item => item.Value.Work));
  }

  private void Append(Response response, string text) {
    lock (gate) {
      if (response.SourceClosed)
        return;
      if (text.Length > MaximumSourceChars - response.Source.Length) {
        // Keep the exact committed prefix, never a cropped Markdown fragment.
        response.SourceClosed = true;
        RequestParse(response);
        if (!pending.ContainsKey(response.Key))
          response.Source = new();
        return;
      }
      response.ImageMarkers += CountImageMarkers(text, response.LastCharacter);
      response.HasReferenceDefinitions |= (response.LastCharacter == ']' && text[0] == ':')
          || text.Contains("]:", StringComparison.Ordinal);
      response.LastCharacter = text[^1];
      response.Source.Append(text);
      if (response.HasReferenceDefinitions || text.AsSpan().ContainsAny(PossibleImageEnds))
        RequestParse(response);
    }
  }

  private static int CountImageMarkers(ReadOnlySpan<char> text, char previous) {
    int count = previous == '!' && text[0] == '[' ? 1 : 0;
    int marker;
    while ((marker = text.IndexOf("![", StringComparison.Ordinal)) >= 0) {
      count++;
      text = text[(marker + 2)..];
    }
    return count;
  }

  // Once all image openers resolve, ordinary suffixes need no more parsing. Reference
  // definitions can acquire a new destination as their unfinished line grows.
  private static bool NeedsParse(Response response) => response.Source.Length > response.ParsedThroughChars
      && response.ImageMarkers > 0 && (response.ImageMarkers > response.ResolvedMarkers || response.HasReferenceDefinitions);

  // Called under the import gate, which is never held while parsing or saving files.
  private void RequestParse(Response response) {
    if (!NeedsParse(response) || response.QueuedBatch is not null)
      return;
    // At capacity, a later saved chunk may get another opportunity. Optional image
    // capture never backpressures transcript writes or grows the queue without bound.
    if (response.RunningBatch is null && pending.Count >= MaximumPendingResponses)
      return;
    response.QueuedBatch = new(TaskCreationOptions.RunContinuationsAsynchronously);
    if (response.RunningBatch is null) {
      response.Work = Task.Run(() => RunAsync(response));
      pending.Add(response.Key, response);
    }
  }

  private async Task RunAsync(Response response) {
    while (true) {
      await Task.Delay(CoalesceInterval);
      await workerSlots.WaitAsync();
      int resolvedMarkers;
      try {
        string source;
        lock (gate) {
          source = response.Source.ToString();
          response.ParsedThroughChars = source.Length;
          response.RunningBatch = response.QueuedBatch;
          response.QueuedBatch = null;
        }
        resolvedMarkers = ImportSnapshot(response, source);
      } finally {
        workerSlots.Release();
      }
      lock (gate) {
        response.ResolvedMarkers = resolvedMarkers;
        response.RunningBatch!.SetResult();
        response.RunningBatch = null;
        bool reachedTargetLimit = response.Attempted.Count >= MaximumTargetsPerResponse;
        if (response.QueuedBatch is not null && !reachedTargetLimit && NeedsParse(response))
          continue;
        response.QueuedBatch?.SetResult();
        response.QueuedBatch = null;
        response.SourceClosed |= reachedTargetLimit;
        if (response.SourceClosed)
          response.Source = new();
        pending.Remove(response.Key);
        return;
      }
    }
  }

  private int ImportSnapshot(Response response, string source) {
    int resolvedMarkers = 0;
    try {
      foreach (MarkdownImageTarget target in MarkdownParser.ReadImageTargets(source)) {
        resolvedMarkers++;
        if (!response.Attempted.Add(target.Key))
          continue;
        storage.SaveImage(response.Execution, response.Id, target, response.WorkingDirectory);
        if (response.Attempted.Count >= MaximumTargetsPerResponse)
          break;
      }
    } catch (Exception) {
      // Deriving optional image attachments must not change the text or the execution outcome.
    }
    return resolvedMarkers;
  }

  // Owned by one recorder. Observe and close run under the session lock; Join runs
  // only after request streaming has returned, without sealing future continuations.
  internal sealed class Observer(MarkdownImageImports imports, ExecutionIdentity execution, string? workingDirectory) {
    private Response? current;

    internal void Observe(LlmResponseChunk chunk) {
      if (current?.Id != chunk.ResponseId)
        CloseResponse();
      if (chunk.Text.Length > 0)
        imports.Append(current ??= new(execution, chunk.ResponseId, workingDirectory), chunk.Text);
      if (chunk.IsFinal)
        CloseResponse();
    }

    internal void CloseResponse() {
      if (current is not { } response)
        return;
      current = null;
      lock (imports.gate)
        imports.RequestParse(response);
    }

    internal Task JoinAsync() {
      CloseResponse();
      return imports.JoinExecutionAsync(execution);
    }
  }

  private sealed class Response(ExecutionIdentity execution, Guid id, string? workingDirectory) {
    internal ExecutionIdentity Execution { get; } = execution;
    internal Guid Id { get; } = id;
    internal string? WorkingDirectory { get; } = workingDirectory;
    internal (Guid SessionId, Guid ExecutionId, Guid ResponseId) Key => (Execution.SessionId, Execution.ExecutionId, Id);
    internal StringBuilder Source { get; set; } = new();
    internal HashSet<string> Attempted { get; } = new(StringComparer.Ordinal);
    internal char LastCharacter { get; set; }
    internal int ImageMarkers { get; set; }
    internal int ResolvedMarkers { get; set; }
    internal bool HasReferenceDefinitions { get; set; }
    internal bool SourceClosed { get; set; }
    internal Task Work { get; set; } = Task.CompletedTask;
    internal TaskCompletionSource? RunningBatch { get; set; }
    internal TaskCompletionSource? QueuedBatch { get; set; }
    internal int ParsedThroughChars { get; set; }
  }
}
