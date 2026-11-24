using System.Text.Json;
using Urd.Backend.Tests.Support;
using Urd.Core.Context;
using Urd.Core.Execution;
using Urd.Core.Storage;
using Xunit;

namespace Urd.Backend.Tests.Context;

public sealed class SubagentContextTests {
  [Fact]
  public void ReadsAnsweredQuestionsAndClaimedBackgroundAnswersBeforeSteeredInput() {
    using var fixture = new ParentHistory();
    fixture.AddAnsweredQuestions();
    SubagentContextEntry[] entries = Entries(fixture.Read());
    Assert.Collection(entries.Where(entry => entry.CallId is not null || entry.Text == "steered human"),
        entry => {
          Assert.Equal(("foreground-question", "ask_user"), (entry.CallId, entry.Tool));
          Assert.Contains("foreground answer", entry.Text, StringComparison.Ordinal);
        },
        entry => {
          Assert.Equal(("background-question", "ask_user"), (entry.CallId, entry.Tool));
          Assert.Contains("background answer", entry.Text, StringComparison.Ordinal);
        },
        entry => Assert.Equal(("human input", "steered human"), (entry.Source, entry.Text)));
  }

  [Fact]
  public void ReadsOnlyAttemptedHumanInputsAcrossCompactionAndLegacyInheritedContext() {
    using var fixture = new ParentHistory();
    string? captured = fixture.Read();
    SubagentContextEntry[] entries = Entries(captured);
    Assert.Collection(entries,
        entry => Assert.Equal(("human input", "old human"), (entry.Source, entry.Text)),
        entry => Assert.Equal(("human input", "current human"), (entry.Source, entry.Text)),
        entry => Assert.Equal(("human input", "inherited human"), (entry.Source, entry.Text)),
        entry => Assert.Equal(("human input", "steered human"), (entry.Source, entry.Text)));
    fixture.AdvanceParent();
    Assert.Equal(captured, fixture.Read());
    Assert.Throws<InvalidOperationException>(() => fixture.Read("prior-tool"));
  }

  private static SubagentContextEntry[] Entries(string? context) {
    Assert.NotNull(context);
    Assert.StartsWith("[PARENT CONTEXT:", context, StringComparison.Ordinal);
    return JsonSerializer.Deserialize<SubagentContextEntry[]>(context[(context.IndexOf('\n', StringComparison.Ordinal) + 1)..],
        JsonSerializerOptions.Web)!;
  }

  private sealed class ParentHistory : IDisposable {
    private const string Result = """{"text":"answer","images":[],"details":null,"isError":false}""";
    private const string EmptyArguments = "{}";
    private const string ForegroundQuestionArguments = """{"question":"foreground question","choices":[]}""";
    private const string BackgroundQuestionArguments = """{"question":"background question","choices":[]}""";
    private readonly TemporaryDatabase storage = new();
    private readonly Database database;
    private readonly SubagentContextReader reader;
    private readonly ExecutionIdentity parent = new(Guid.NewGuid(), Guid.NewGuid(), 0);
    private readonly Guid old = Guid.NewGuid();
    private readonly Guid retained = Guid.NewGuid();
    private readonly Guid invoking = Guid.NewGuid();
    private readonly Guid later = Guid.NewGuid();
    private readonly Guid initial = Guid.NewGuid();
    private readonly Guid boundary = Guid.NewGuid();

    internal ParentHistory() {
      UrdStore.Open(storage.Path).Dispose();
      storage.Execute($"""
        INSERT INTO sessions (session_id, history_revision) VALUES ('{parent.SessionId}', 0), ('{Sender}', 0);
        {Decision(initial, "old human")}
        INSERT INTO execution_runs (ordinal, session_id, run_id, history_revision, input_id)
        VALUES (1, '{parent.SessionId}', '{parent.ExecutionId}', 0, '{initial}');
        INSERT INTO execution_requests (request_id, run_id, request_index, attempt, outcome) VALUES
          ('{old}', '{parent.ExecutionId}', 0, 2, 0), ('{retained}', '{parent.ExecutionId}', 1, 2, 0),
          ('{invoking}', '{parent.ExecutionId}', 2, 2, 0), ('{later}', '{parent.ExecutionId}', 3, 1, NULL);
        {Compaction(boundary, retained, 1, "old compaction summary")}
        UPDATE execution_requests SET compaction_id = '{boundary}' WHERE request_id = '{invoking}';
        {Input(initial, old)}
        {NewInput(retained, "current human")}
        {NewInput(retained, "current agent", Sender)}
        {LegacyInheritedInput(retained)}
        {NewInput(invoking, "steered human")}
        {NewInput(later, "not attempted yet")}
        INSERT INTO execution_output (request_id, chunk_index, text_utf8, is_final) VALUES
          ('{old}', 0, CAST('old assistant' AS BLOB), 1),
          ('{retained}', 0, CAST('parent assistant' AS BLOB), 1),
          ('{invoking}', 0, CAST('invoking assistant' AS BLOB), 1);
        INSERT INTO execution_tools (run_id, request_id, call_index, call_id, name, started, background, outcome,
            arguments_json, result_json) VALUES
          ('{parent.ExecutionId}', '{retained}', 0, 'prior-tool', 'read', 1, 0, 0, '{EmptyArguments}',
              '{Result.Replace("answer", "prior result", StringComparison.Ordinal)}'),
          ('{parent.ExecutionId}', '{invoking}', 0, 'delegating', 'subagent', 1, 1, NULL, '{EmptyArguments}', NULL),
          ('{parent.ExecutionId}', '{invoking}', 1, 'sibling-tool', 'read', 1, 0, 0, '{EmptyArguments}', '{Result}');
        """);
      database = new(storage.OpenReadOnlyConnection());
      reader = new(database);
    }

    internal Guid Sender { get; } = Guid.NewGuid();

    internal void AddAnsweredQuestions() {
      Guid completion = Guid.NewGuid();
      storage.Execute($"""
        INSERT INTO execution_tools (run_id, request_id, call_index, call_id, name, started, background, outcome,
            arguments_json, result_json) VALUES
          ('{parent.ExecutionId}', '{old}', 0, 'foreground-question', 'ask_user', 1, 0, 0,
              '{ForegroundQuestionArguments}', '{Result.Replace("answer", "foreground answer", StringComparison.Ordinal)}'),
          ('{parent.ExecutionId}', '{old}', 1, 'background-question', 'ask_user', 1, 1, 0,
              '{BackgroundQuestionArguments}', '{Result.Replace("answer", "background answer", StringComparison.Ordinal)}');
        INSERT INTO execution_tool_completions
            (completion_id, session_id, history_revision, request_id, call_id, created_at, claimed_request_id, published)
        VALUES ('{completion}', '{parent.SessionId}', 0, '{old}', 'background-question',
            '2026-11-12T00:00:00+00:00', '{invoking}', 1);
        INSERT INTO execution_messages
            (session_id, history_revision, run_id, role, message_id, request_id, call_id, tool_name)
        VALUES ('{parent.SessionId}', 0, '{parent.ExecutionId}', 2, '{Guid.NewGuid()}', '{old}', 'foreground-question', 'ask_user'),
            ('{parent.SessionId}', 0, '{parent.ExecutionId}', 2, '{Guid.NewGuid()}', '{old}', 'background-question', 'ask_user'),
            ('{parent.SessionId}', 0, '{parent.ExecutionId}', 3, '{completion}', '{old}', 'background-question', 'ask_user');
        """);
    }

    internal string? Read(string callId = "delegating") => reader.Read(parent, invoking, callId);

    internal void AdvanceParent() => storage.Execute($"""
      {Compaction(Guid.NewGuid(), invoking, 2, "new compaction summary")}
      UPDATE execution_requests SET attempt = 2 WHERE request_id = '{later}';
      UPDATE execution_tools SET outcome = 0, result_json = '{Result}' WHERE call_id = 'delegating';
      """);

    private string Decision(Guid input, string text) => $"""
      INSERT INTO input_decisions (session_id, input_id, expected_history_revision, original_text_utf8,
          decision, run_id, llm_selection_json)
      VALUES ('{parent.SessionId}', '{input}', 0, CAST('{text}' AS BLOB), 1, '{parent.ExecutionId}', '{EmptyArguments}');
      """;

    private string Input(Guid input, Guid request, Guid? sender = null) => $"""
      INSERT INTO execution_inputs (session_id, input_id, run_id, request_id, provenance, sender_session_id)
      VALUES ('{parent.SessionId}', '{input}', '{parent.ExecutionId}', '{request}',
          {(sender is null ? 0 : 1)}, {(sender is null ? "NULL" : $"'{sender}'")});
      """;

    private string NewInput(Guid request, string text, Guid? sender = null) {
      Guid input = Guid.NewGuid();
      return Decision(input, text) + Input(input, request, sender);
    }

    private string LegacyInheritedInput(Guid request) {
      Guid input = Guid.NewGuid();
      string text = SubagentContext.Build([
        new("human input", "inherited human"), new("assistant", "legacy assistant"),
        new("tool result", "legacy tool result"), new("agent assignment", "legacy assignment")
      ])!;
      return Decision(input, text) + $"""
        INSERT INTO execution_inputs (session_id, input_id, run_id, request_id, provenance, sender_session_id)
        VALUES ('{parent.SessionId}', '{input}', '{parent.ExecutionId}', '{request}', 2, '{Sender}');
        """;
    }

    private string Compaction(Guid id, Guid firstKept, int requestIndex, string summary) => $"""
      INSERT INTO session_compactions (compaction_id, session_id, history_revision, run_id,
          first_kept_request_id, first_kept_message_index, first_kept_run_ordinal, first_kept_request_index,
          reason, tokens_before, tokens_after, created_at, summary)
      VALUES ('{id}', '{parent.SessionId}', 0, '{parent.ExecutionId}', '{firstKept}', 0, 1, {requestIndex},
          0, 100, 50, 'now', '{summary}');
      """;

    public void Dispose() {
      database.Dispose();
      storage.Dispose();
    }
  }
}
