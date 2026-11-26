using System.Text;
using System.Text.Json;

namespace Urd.Core.Tools;

// Reads bounded text pages or processed images after preceding Urd mutations finish.
internal sealed class ReadTool(FileMutationQueue files) : AgentTool {
  public override string Name => "read";
  public override ToolRelevance RelevantWhen => ToolRelevance.Always;
  public override string Description => "Read UTF-8 text or a supported image. Prefer this to shell commands for inspecting file contents. Use offset/limit to page through large text files.";
  public override JsonElement ParametersSchema { get; } = ToolSchema.Parse("""
    {
      "type": "object",
      "properties": {
        "path": { "type": "string", "description": "Path to the file to read (relative or absolute)" },
        "offset": { "type": "integer", "minimum": 1, "description": "Line number to start reading from (1-indexed). Omit to start at line 1." },
        "limit": { "type": "integer", "minimum": 1, "description": "Maximum number of lines to read" }
      },
      "required": ["path"]
    }
    """);

  public override async Task<ToolOutput> RunAsync(JsonElement arguments, ToolContext context, CancellationToken cancellation) {
    try {
      string path = ToolArguments.RequireString(arguments, "path");
      int offset = ToolArguments.OptionalInteger(arguments, "offset", 1, 1);
      int limit = ToolArguments.OptionalInteger(arguments, "limit", 1, int.MaxValue);
      string absolutePath = ToolPaths.Resolve(path, context.WorkingDirectory);
      // Admission precedes any I/O await, so reads observe earlier mutations in the reply.
      return await files.ReadAfterMutationsAsync(absolutePath,
        token => FileReader.ReadAsync(absolutePath, offset, limit, context.ModelAcceptsImages, token), cancellation).ConfigureAwait(false);
    } catch (OperationCanceledException) {
      return new ToolOutput("read cancelled.", [], IsError: true);
    } catch (DecoderFallbackException) {
      return new ToolOutput("read failed: file is not valid UTF-8 text or a supported image. Use bash for binary inspection.", [], IsError: true);
    } catch (Exception error) when (ToolFailures.IsExpected(error)) {
      return new ToolOutput($"read failed: {error.Message}", [], IsError: true);
    }
  }
}
