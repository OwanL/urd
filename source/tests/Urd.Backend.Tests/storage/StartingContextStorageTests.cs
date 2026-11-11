using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Urd.Backend.Tests.Support;
using Urd.Core.Context;
using Urd.Core.Storage;
using Xunit;

namespace Urd.Backend.Tests.Storage;

public sealed class StartingContextStorageTests {
  [Fact]
  public void OrderedInstructionsAndToolDeclarationsSurviveReopen() {
    using var database = new TemporaryDatabase();
    Guid session;
    const string firstSchema = "{\"type\":\"object\",\"properties\":{\"zeta\":{\"type\":\"string\"},\"alpha\":{\"type\":\"integer\"}},\"$defs\":{\"Later\":{\"type\":\"boolean\"},\"Earlier\":{\"type\":\"null\"}}}";
    const string secondSchema = "{\"type\":\"array\",\"items\":{\"type\":\"string\"}}";
    StartingContext expected = Context(
        "You are Urd — assist with café requests. 🌍\nKeep this exact.",
        "Developer instruction: naïve façade; preserve order.",
        Tool("zeta_tool", "First tool — exact description.", firstSchema),
        Tool("alpha_tool", "Second tool.", secondSchema));
    expected.Instructions[0].Contents.Add(new TextContent("Another text block.\n"));

    using (UrdStore store = UrdStore.Open(database.Path))
      session = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
    Save(database.Path, session, expected);

    using UrdStore reopened = UrdStore.Open(database.Path);
    StartingContext restored = Assert.IsType<StartingContext>(reopened.StartingContexts.Read(session));
    Assert.True(((IList<ChatMessage>)restored.Instructions).IsReadOnly);
    Assert.True(((IList<AIFunctionDeclaration>)restored.Tools).IsReadOnly);
    Assert.Equal(new[] { ChatRole.System, new ChatRole("developer") }, restored.Instructions.Select(message => message.Role));
    Assert.Equal(expected.Instructions.Select(message => message.Text), restored.Instructions.Select(message => message.Text));
    Assert.Equal(expected.Instructions[0].Contents.OfType<TextContent>().Select(content => content.Text),
        restored.Instructions[0].Contents.OfType<TextContent>().Select(content => content.Text));
    Assert.Equal(new[] { "zeta_tool", "alpha_tool" }, restored.Tools.Select(tool => tool.Name));
    Assert.Equal(new[] { "First tool — exact description.", "Second tool." },
        restored.Tools.Select(tool => tool.Description));
    Assert.Equal(new[] { firstSchema, secondSchema }, restored.Tools.Select(tool => tool.JsonSchema.GetRawText()));
  }

  [Fact]
  public void FirstSavedStartingContextIsNotOverwritten() {
    using var database = new TemporaryDatabase();
    Guid session;
    StartingContext first = Context("Initial system prompt.", "Initial developer prompt.",
        Tool("initial_tool", "Initial declaration.", "{\"type\":\"object\"}"));
    StartingContext later = Context("Changed system prompt.", "Changed developer prompt.",
        Tool("later_tool", "Changed declaration.", "{\"type\":\"array\"}"));

    using (UrdStore store = UrdStore.Open(database.Path))
      session = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
    Save(database.Path, session, first);
    Save(database.Path, session, later);

    using UrdStore reopened = UrdStore.Open(database.Path);
    StartingContext restored = Assert.IsType<StartingContext>(reopened.StartingContexts.Read(session));
    Assert.Equal("Initial system prompt.", restored.Instructions[0].Text);
    Assert.Equal("Initial developer prompt.", restored.Instructions[1].Text);
    Assert.Equal("initial_tool", Assert.Single(restored.Tools).Name);
  }

  [Fact]
  public void SaveRejectsUnsupportedRolesAndNontextInstructions() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    Guid session = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;

    var unsupportedRole = new StartingContext(
        Array.AsReadOnly(new[] { new ChatMessage(ChatRole.User, "not an instruction") }),
        Array.AsReadOnly(Array.Empty<AIFunctionDeclaration>()));
    Assert.Throws<ArgumentException>(() => Save(database.Path, session, unsupportedRole));

    var nontextMessage = new ChatMessage(ChatRole.System, "text plus a function call");
    nontextMessage.Contents.Add(new FunctionCallContent("call", "tool", new Dictionary<string, object?>()));
    var nontextInstruction = new StartingContext(
        Array.AsReadOnly(new[] { nontextMessage }), Array.AsReadOnly(Array.Empty<AIFunctionDeclaration>()));
    Assert.Throws<ArgumentException>(() => Save(database.Path, session, nontextInstruction));
    Assert.Null(store.StartingContexts.Read(session));
  }

  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public void RejectedOrRolledBackRequestStartDoesNotSaveContext(bool failPlacement) {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    Guid session = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
    Guid execution = Guid.NewGuid();
    store.UserMessages.Admit(session, 0, new(Guid.NewGuid(), 0, "request", TestLlmSelection.Default),
        true, execution, null);
    Guid request = store.Executions.ClaimNextRequest(session, execution, 0)!.RequestId;
    StartingContext context = Context("system", "developer");
    if (failPlacement) {
      database.Execute("""
        CREATE TRIGGER fail_placement BEFORE INSERT ON execution_messages BEGIN
          SELECT RAISE(ABORT, 'placement failed');
        END;
        """);
      Assert.Throws<SqliteException>(() => store.Executions.StartRequest(session, execution, 0, request, context));
    } else {
      store.Executions.RequestStop(session, execution, ownsExecution: true);
      Assert.False(store.Executions.StartRequest(session, execution, 0, request, context));
    }
    Assert.Null(store.StartingContexts.Read(session));
  }

  private static StartingContext Context(string system, string developer, params AIFunctionDeclaration[] tools) => new(
      Array.AsReadOnly(new[] { new ChatMessage(ChatRole.System, system), new ChatMessage(new ChatRole("developer"), developer) }),
      Array.AsReadOnly(tools));

  private static AIFunctionDeclaration Tool(string name, string description, string schema) {
    using JsonDocument document = JsonDocument.Parse(schema);
    return AIFunctionFactory.CreateDeclaration(name, description, document.RootElement.Clone());
  }

  private static void Save(string databasePath, Guid session, StartingContext context) {
    using var connection = new SqliteConnection(new SqliteConnectionStringBuilder {
      DataSource = databasePath,
      Pooling = false
    }.ToString());
    connection.Open();
    using (SqliteCommand command = connection.CreateCommand()) {
      command.CommandText = "PRAGMA foreign_keys = ON;";
      command.ExecuteNonQuery();
    }
    using var database = new Database(connection);
    var storage = new StartingContextStorage(database);
    database.Write(transaction => {
      storage.Save(transaction, session, context);
      return true;
    });
  }
}
