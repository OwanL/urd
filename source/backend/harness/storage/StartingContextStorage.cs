using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Urd.Core.Context;

namespace Urd.Core.Storage;

// Stores the immutable initial instructions and tool declarations for one session.
internal sealed class StartingContextStorage(Database database) {
  internal StartingContext? Read(Guid sessionId) => database.Read(transaction => {
    using SqliteCommand command = database.Command(transaction,
        "SELECT context_json FROM session_starting_context WHERE session_id = $session;", ("$session", sessionId));
    if (command.ExecuteScalar() is not string json)
      return null;

    return Restore(json);
  });

  internal void Save(SqliteTransaction transaction, Guid sessionId, StartingContext context) {
    ArgumentNullException.ThrowIfNull(context);
    using SqliteCommand existing = database.Command(transaction,
        "SELECT EXISTS(SELECT 1 FROM session_starting_context WHERE session_id = $session);", ("$session", sessionId));
    if ((long)existing.ExecuteScalar()! != 0)
      return;

    var document = new StartingContextDocument(
        context.Instructions.Select(ToInstruction).ToArray(), context.Tools.Select(ToTool).ToArray());
    string json = JsonSerializer.Serialize(document);

    using SqliteCommand command = database.Command(transaction, """
      INSERT INTO session_starting_context (session_id, context_json)
      VALUES ($session, $context)
      ON CONFLICT(session_id) DO NOTHING;
      """, ("$session", sessionId), ("$context", json));
    command.ExecuteNonQuery();
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

  private static StartingContext Restore(string json) {
    StartingContextDocument? document;
    try {
      document = JsonSerializer.Deserialize<StartingContextDocument>(json);
    } catch (JsonException error) {
      throw new InvalidDataException("The stored starting context is invalid.", error);
    }

    if (document?.Instructions is null || document.Tools is null
        || document.Instructions.Any(instruction => instruction is null)
        || document.Tools.Any(tool => tool is null))
      throw new InvalidDataException("The stored starting context is incomplete.");

    ChatMessage[] instructions = document.Instructions.Select(ReadInstruction).ToArray();
    AIFunctionDeclaration[] tools = document.Tools.Select(ReadTool).ToArray();
    return new(Array.AsReadOnly(instructions), Array.AsReadOnly(tools));
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

    return AIFunctionFactory.CreateDeclaration(tool.Name, tool.Description, tool.JsonSchema.Clone());
  }

  private sealed record StartingContextDocument(InstructionDocument?[]? Instructions, ToolDeclarationDocument?[]? Tools);
  private sealed record InstructionDocument(string? Role, string?[]? Contents);
  private sealed record ToolDeclarationDocument(string? Name, string? Description, JsonElement JsonSchema);
}
