using System.Text;
using System.Text.Json;

namespace Urd.Core.Tools;

// Creates or replaces a UTF-8 file, ordered with other Urd mutations of that path.
internal sealed class WriteTool(FileMutationQueue files) : AgentTool {
  public override string Name => "write";
  public override ToolRelevance RelevantWhen { get; } = ToolRelevance.When("""
    The task requires creating new files or fully replacing existing file contents.
    """);
  public override string Description => """
    Write content to a file. Creates the file if it doesn't exist, overwrites if it does.
    Automatically creates parent directories. Use only for new files or complete rewrites.
    """;
  public override JsonElement ParametersSchema { get; } = ToolSchema.Parse("""
    {
      "type": "object",
      "properties": {
        "path": { "type": "string", "description": "Path to the file to write (relative or absolute)" },
        "content": { "type": "string", "description": "Content to write to the file" }
      },
      "required": ["path", "content"]
    }
    """);
  private static readonly UTF8Encoding utf8 = new(false, true);

  public override async Task<ToolOutput> RunAsync(JsonElement arguments, ToolContext context, CancellationToken cancellation) {
    try {
      string path = ToolArguments.String(arguments, "path");
      string content = ToolArguments.String(arguments, "content");
      string absolutePath = ToolPaths.Resolve(path, context.WorkingDirectory);
      // Admission is synchronous: no earlier await may reorder calls from one reply.
      return await files.ChangeAsync(absolutePath, async token => {
        token.ThrowIfCancellationRequested();
        byte[] bytes = utf8.GetBytes(content);
        Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
        token.ThrowIfCancellationRequested();
        await File.WriteAllBytesAsync(absolutePath, bytes, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        return new ToolOutput($"Successfully wrote {bytes.Length} bytes to {path}", []);
      }, cancellation).ConfigureAwait(false);
    } catch (OperationCanceledException) {
      return new ToolOutput("write cancelled. The file may be partly written; cancellation does not roll back changes.", [], IsError: true);
    } catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or KeyNotFoundException or NotSupportedException) {
      return new ToolOutput($"write failed: {error.Message}", [], IsError: true);
    }
  }
}
