using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Urd.Core.Context;
using Urd.Core.Providers;
using Urd.Core.Tools;

namespace Urd.Core.Storage;

// Stores immutable ordered starting-context snapshots for each session boundary.
internal sealed class StartingContextStorage(Database database) {
  internal StartingContext? Read(Guid sessionId) => database.Read(transaction => {
    Guid? activatedBoundary = database.QuerySingleOrDefault<Guid?>(transaction, """
      SELECT boundary_id FROM session_starting_context_active WHERE session_id = $session;
      """, row => row.NullableGuid("boundary_id"), ("$session", sessionId));
    if (activatedBoundary is { } active && (active == Guid.Empty || database.Scalar<bool>(transaction, """
        SELECT EXISTS (SELECT 1 FROM session_compactions c JOIN sessions s ON s.session_id = c.session_id
            WHERE c.session_id = $session AND c.compaction_id = $boundary AND c.history_revision = s.history_revision);
        """, ("$session", sessionId), ("$boundary", active)))) {
      StartingContext? current = Read(transaction, sessionId, active);
      if (current is not null)
        return current;
    }

    Guid latestBoundary = database.QuerySingleOrDefault(transaction, """
      SELECT c.compaction_id AS boundary_id
      FROM sessions s LEFT JOIN session_compactions c ON c.session_id = s.session_id
          AND c.history_revision = s.history_revision
      WHERE s.session_id = $session
      ORDER BY c.ordinal DESC LIMIT 1;
      """, row => row.NullableGuid("boundary_id"), ("$session", sessionId)) ?? Guid.Empty;
    return Read(transaction, sessionId, latestBoundary);
  });

  internal StartingContext? Read(Guid sessionId, Guid boundaryId) =>
      database.Read(transaction => Read(transaction, sessionId, boundaryId));

  internal StartingContext? Read(SqliteTransaction transaction, Guid sessionId, Guid boundaryId) {
    string? json = database.Scalar<string?>(transaction, """
      SELECT context_json FROM session_starting_context
      WHERE session_id = $session AND boundary_id = $boundary;
      """, ("$session", sessionId), ("$boundary", boundaryId));
    return json is null ? null : Restore(json, boundaryId);
  }

  // StartRequest calls this only after its transaction has validated the current boundary.
  // The first snapshot for a boundary remains authoritative for its entire lifetime.
  internal void Activate(SqliteTransaction transaction, Guid sessionId, StartingContext context) {
    ArgumentNullException.ThrowIfNull(context);
    var document = new StartingContextDocument(
        context.Instructions.Select(ToInstruction).ToArray(), context.Tools.Select(ToTool).ToArray(),
        context.InstructionPaths?.ToArray());
    string json = JsonSerializer.Serialize(document);

    database.Execute(transaction, """
      INSERT INTO session_starting_context (session_id, boundary_id, context_json)
      VALUES ($session, $boundary, $context)
      ON CONFLICT(session_id, boundary_id) DO NOTHING;
      INSERT INTO session_starting_context_active (session_id, boundary_id)
      VALUES ($session, $boundary)
      ON CONFLICT(session_id) DO UPDATE SET boundary_id = excluded.boundary_id;
      """, ("$session", sessionId), ("$boundary", context.BoundaryId), ("$context", json));
  }

  private static InstructionDocument ToInstruction(ChatMessage message) {
    ArgumentNullException.ThrowIfNull(message);
    if (message.Role != ChatRole.System && message.Role != new ChatRole("developer"))
      throw new ArgumentException("Starting-context instructions support only system and developer roles.", nameof(message));

    var contents = new List<string>(message.Contents.Count);
    foreach (AIContent content in message.Contents) {
      if (content is not TextContent { Text: { } text })
        throw new ArgumentException("Starting-context instructions must contain only text content.", nameof(message));
      contents.Add(text);
    }
    return new(message.Role.ToString(), contents.ToArray());
  }

  private static ToolDeclarationDocument ToTool(AIFunctionDeclaration tool) {
    ArgumentNullException.ThrowIfNull(tool);
    if (tool.JsonSchema.ValueKind == JsonValueKind.Undefined)
      throw new ArgumentException("Starting-context tools require a JSON schema.", nameof(tool));

    return new(tool.Name, tool.Description, tool.JsonSchema.Clone());
  }

  private static StartingContext Restore(string json, Guid boundaryId) {
    StartingContextDocument? document;
    try {
      document = JsonSerializer.Deserialize<StartingContextDocument>(json);
    } catch (JsonException error) {
      throw new InvalidDataException("The stored starting context is invalid.", error);
    }

    if (document?.Instructions is null || document.Tools is null
        || document.Instructions.Any(instruction => instruction is null)
        || document.Tools.Any(tool => tool is null)
        || document.InstructionPaths?.Any(path => path is null) == true)
      throw new InvalidDataException("The stored starting context is incomplete.");

    ChatMessage[] instructions = document.Instructions.Select(ReadInstruction).ToArray();
    AIFunctionDeclaration[] tools = document.Tools.Select(ReadTool).ToArray();
    return new(Array.AsReadOnly(instructions), Array.AsReadOnly(tools)) {
      BoundaryId = boundaryId,
      InstructionPaths = document.InstructionPaths?.Select(path => path!).ToArray().AsReadOnly()
    };
  }

  private static ChatMessage ReadInstruction(InstructionDocument? instruction) {
    if (instruction?.Contents is null || instruction.Contents.Any(text => text is null))
      throw new InvalidDataException("The stored starting-context instruction is incomplete.");
    if (instruction.Role is not ("system" or "developer"))
      throw new InvalidDataException("The stored starting-context instruction has an unsupported role.");

    return new(new ChatRole(instruction.Role), instruction.Contents.Select(text => (AIContent)new TextContent(text)).ToList());
  }

  private static AIFunctionDeclaration ReadTool(ToolDeclarationDocument? tool) {
    if (tool is null || tool.Name is null || tool.JsonSchema.ValueKind == JsonValueKind.Undefined)
      throw new InvalidDataException("The stored starting-context tool declaration is incomplete.");

    // Restore the current subagent contract without rewriting the boundary or historical calls.
    if (tool.Name == SubagentTool.ToolName && tool.JsonSchema.ValueKind == JsonValueKind.Object
        && tool.JsonSchema.TryGetProperty("properties", out JsonElement properties)
        && properties.ValueKind == JsonValueKind.Object && properties.TryGetProperty("context", out _))
      return AgentToolDeclaration.Create(new SubagentTool());
    return AIFunctionFactory.CreateDeclaration(tool.Name, tool.Description, tool.JsonSchema.Clone());
  }

  // InstructionPaths is absent from snapshots saved before subagents inherited instruction selection.
  private sealed record StartingContextDocument(InstructionDocument?[]? Instructions, ToolDeclarationDocument?[]? Tools,
      string?[]? InstructionPaths = null);
  private sealed record InstructionDocument(string? Role, string?[]? Contents);
  private sealed record ToolDeclarationDocument(string? Name, string? Description, JsonElement JsonSchema);
}
