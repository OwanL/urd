using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Urd.Backend.Tests.Support;
using Urd.Core.Execution;
using Urd.Core.Sessions;
using Urd.Core.Storage;
using Xunit;

namespace Urd.Backend.Tests.Storage;

public sealed class TranscriptPositionWriterTests {
  [Fact]
  public void PromotionStreamingAndToolHooksMaintainPositionsWithoutDuplicatingRetries() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    Guid session = store.Sessions.CreatePrimary(Guid.NewGuid()).Id, run = Guid.NewGuid();
    UserMessageSubmission first = Input("🙂\0a"), second = Input("");
    Admit(store, session, run, first);
    Admit(store, session, run, second);
    Guid request = store.Executions.ClaimNextRequest(session, run, 0)!.RequestId;
    Assert.Equal(0, State(store, session).EntryCount);
    Assert.Equal(0, State(store, session).PositionVersion);
    Assert.True(store.Executions.StartRequest(session, run, 0, request));
    TranscriptPositionWindow users = store.Positions.Locate(session, 0, 0);
    Assert.Equal(new long[] { 262, 256 }, users.Entries.Select(entry => entry.Position.Weight));
    Assert.True(store.Executions.StartRequest(session, run, 0, request));
    Assert.Equal(users.State, State(store, session));
    Assert.Equal(new[] { first.SubmissionId, second.SubmissionId },
        store.Positions.Prompts(session, 0).Prompts.Select(prompt => prompt.Entry.EntryId));

    Guid output = Guid.NewGuid();
    var firstChunk = new LlmResponseChunk(request, output, 0, "🙂", false);
    Assert.True(store.LlmResponses.Append(session, run, 0, firstChunk));
    TranscriptPositionWindow initial = store.Positions.Locate(session, 0, long.MaxValue);
    TranscriptPosition text = initial.Target!;
    Assert.Equal(260, text.Weight);
    Assert.False(store.LlmResponses.Append(session, run, 0, firstChunk));
    Assert.Equal(initial.State, State(store, session));
    Assert.True(store.LlmResponses.Append(session, run, 0, new(request, output, 1, "\0x", false)));
    TranscriptPositionWindow grown = store.Positions.Locate(session, 0, long.MaxValue);
    Assert.Equal(text.Entry, grown.Target!.Entry);
    Assert.Equal(text.Rank, grown.Target.Rank);
    Assert.Equal(text.PrefixWeight, grown.Target.PrefixWeight);
    Assert.Equal(262, grown.Target.Weight);
    Assert.True(grown.State.PositionVersion > initial.State.PositionVersion);
    Assert.Equal(0, grown.State.HistoryRevision);

    Assert.True(store.LlmResponses.ObserveTool(session, run, 0, request, "call", null));
    TranscriptPositionWindow tool = store.Positions.Locate(session, 0, long.MaxValue);
    Assert.Equal(TranscriptEntryKind.Tool, tool.Target!.Entry.Kind);
    Assert.Equal(256, tool.Target.Weight);
    Assert.True(store.LlmResponses.ObserveTool(session, run, 0, request, "call", "named"));
    Assert.True(store.LlmResponses.ObserveTool(session, run, 0, request, "call", "named"));
    Assert.Equal(tool.State, State(store, session)); // Name does not change the compact-row weight.
    Guid nextText = Guid.NewGuid();
    Assert.True(store.LlmResponses.Append(session, run, 0, new(request, nextText, 2, "tail", false)));
    TranscriptPositionWindow beforeFinal = store.Positions.Locate(session, 0, long.MaxValue);
    Assert.True(store.LlmResponses.Append(session, run, 0, new(request, nextText, 3, "", true)));
    TranscriptPositionWindow final = store.Positions.Locate(session, 0, long.MaxValue);
    Assert.Equal(beforeFinal.State.TotalWeight, final.State.TotalWeight);
    Assert.True(final.State.PositionVersion > beforeFinal.State.PositionVersion);
    Assert.Equal(4, final.Entries[^1].Descriptor.Text!.AvailableThrough.SegmentIndex);
    var completion = new LlmRequestResult(request, ExecutionOutcome.Completed, ToolCalls: [Call("call"), Call("unobserved")]);
    Assert.True(store.LlmResponses.CompleteRequest(session, run, 0, completion));
    TranscriptPositionState completed = State(store, session);
    Assert.Equal(final.State.EntryCount + 1, completed.EntryCount);
    Assert.True(store.LlmResponses.CompleteRequest(session, run, 0, completion));
    Assert.Equal(completed, State(store, session));
    AssertPrefixes(store.Positions.Locate(session, 0, long.MaxValue));
  }

  [Fact]
  public void WeightsDependOnSourceBytesNotChunkingAndSurviveReopen() {
    using var database = new TemporaryDatabase();
    Guid session;
    TranscriptPositionState saved;
    using (UrdStore store = UrdStore.Open(database.Path)) {
      Guid first = Write(["a🙂\n\0z"]), second = Write(["a", "🙂", "\n", "\0z"]);
      TranscriptPositionWindow whole = store.Positions.Locate(first, 0, long.MaxValue);
      TranscriptPositionWindow split = store.Positions.Locate(second, 0, long.MaxValue);
      Assert.Equal(whole.State.TotalWeight, split.State.TotalWeight);
      Assert.Equal(whole.Target!.Weight, split.Target!.Weight);
      Assert.Equal(256 + Encoding.UTF8.GetByteCount("a🙂\n\0z"), whole.Target.Weight);
      session = second;
      saved = split.State;

      Guid Write(string[] chunks) {
        Guid id = store.Sessions.CreatePrimary(Guid.NewGuid()).Id, run = Guid.NewGuid(), output = Guid.NewGuid();
        Admit(store, id, run, Input("input"));
        Guid request = store.Executions.ClaimNextRequest(id, run, 0)!.RequestId;
        Assert.True(store.Executions.StartRequest(id, run, 0, request));
        for (int index = 0; index < chunks.Length; index++)
          Assert.True(store.LlmResponses.Append(id, run, 0, new(request, output, index, chunks[index], index == chunks.Length - 1)));
        return id;
      }
    }
    using UrdStore reopened = UrdStore.Open(database.Path);
    Assert.Equal(saved, State(reopened, session));
  }

  [Fact]
  public void PositionFailureRollsBackPromotionAndOutputIncludingCommitFailure() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    Guid session = store.Sessions.CreatePrimary(Guid.NewGuid()).Id, run = Guid.NewGuid(), output = Guid.NewGuid();
    Admit(store, session, run, Input("one"));
    Admit(store, session, run, Input("two"));
    Guid request = store.Executions.ClaimNextRequest(session, run, 0)!.RequestId;
    database.Execute("""
      CREATE TRIGGER reject_position BEFORE INSERT ON transcript_positions WHEN NEW.rank = 1
      BEGIN SELECT RAISE(ABORT, 'position rejected'); END;
      """);
    Assert.Throws<SqliteException>(() => store.Executions.StartRequest(session, run, 0, request));
    Assert.Equal(0, State(store, session).EntryCount);
    Assert.Equal(0, State(store, session).PositionVersion);
    Assert.Equal(1L, database.Scalar("SELECT attempt FROM execution_requests;")); // raw-sql: Verify durable request state after a simulated failure.
    Assert.Equal(0L, database.Scalar("SELECT COUNT(*) FROM execution_messages;")); // raw-sql: Verify no history row survived rollback.
    database.Execute("DROP TRIGGER reject_position;");
    Assert.True(store.Executions.StartRequest(session, run, 0, request));
    Assert.True(store.LlmResponses.Append(session, run, 0, new(request, output, 0, "a", false)));
    TranscriptPositionState before = State(store, session);
    database.Execute("""
      CREATE TABLE test_parent (id INTEGER PRIMARY KEY);
      CREATE TABLE test_child (parent_id INTEGER REFERENCES test_parent(id) DEFERRABLE INITIALLY DEFERRED);
      CREATE TRIGGER fail_position_commit AFTER UPDATE ON transcript_positions
      BEGIN INSERT INTO test_child VALUES (1); END;
      """);
    Assert.Throws<SqliteException>(() => store.LlmResponses.Append(session, run, 0, new(request, output, 1, "b", false)));
    Assert.Equal(before, State(store, session));
    Assert.Single(StoredLlmResponses.Read(database));
    Assert.Equal(1L, database.Scalar("SELECT next_chunk_index FROM execution_requests;")); // raw-sql: Verify the commit failure rolled back the chunk cursor.
    database.Execute("DROP TRIGGER fail_position_commit;");
    Assert.True(store.LlmResponses.Append(session, run, 0, new(request, output, 1, "b", false)));
    Assert.Equal(before.TotalWeight + 1, State(store, session).TotalWeight);
  }

  [Fact]
  public void HistoricalWeightWritesFailAtomicallyAndStaleWritersDoNotChangePositions() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    Guid session = store.Sessions.CreatePrimary(Guid.NewGuid()).Id, run = Guid.NewGuid();
    Admit(store, session, run, Input("user"));
    Guid request = store.Executions.ClaimNextRequest(session, run, 0)!.RequestId;
    Assert.True(store.Executions.StartRequest(session, run, 0, request));
    Guid output = Guid.NewGuid();
    Assert.True(store.LlmResponses.Append(session, run, 0, new(request, output, 0, "a", false)));
    TranscriptPositionWindow before = store.Positions.Locate(session, 0, 0);
    Assert.Throws<SqliteException>(() => database.Execute($"""
      UPDATE execution_messages SET layout_weight = layout_weight + 1
      WHERE session_id = '{session:D}' AND role = 0;
      """));
    Assert.Equal(before.State, State(store, session));
    Assert.Equal(before.Entries[0].Position.Weight,
        store.Positions.Locate(session, 0, 0).Entries[0].Position.Weight);
    database.ForceHistoryRevision(1);
    TranscriptPositionState revised = store.Positions.Locate(session, 1, 0).State;
    Assert.True(revised.PositionVersion > before.State.PositionVersion);
    Assert.Equal(0, revised.EntryCount);
    Assert.False(store.LlmResponses.Append(session, run, 0, new(request, output, 1, "b", false)));
    Assert.False(store.LlmResponses.ObserveTool(session, run, 0, request, "stale", null));
    Assert.False(store.Executions.StartRequest(session, run, 0, request));
    Assert.Equal(revised, store.Positions.Locate(session, 1, 0).State);
  }

  [Fact]
  public async Task PromptActivationKeepsStateWeightsAndDescriptorsInOneSnapshotDuringStreaming() {
    using var database = new TemporaryDatabase();
    using UrdStore writer = UrdStore.Open(database.Path);
    Guid session = writer.Sessions.CreatePrimary(Guid.NewGuid()).Id, run = Guid.NewGuid(), output = Guid.NewGuid();
    UserMessageSubmission input = Input("u");
    Admit(writer, session, run, input);
    Guid request = writer.Executions.ClaimNextRequest(session, run, 0)!.RequestId;
    Assert.True(writer.Executions.StartRequest(session, run, 0, request));
    var prompt = new TranscriptEntryRef(run, input.SubmissionId, TranscriptEntryKind.User, 0);
    using UrdStore reader = UrdStore.Open(database.Path);
    using var start = new ManualResetEventSlim();
    Task appends = Task.Run(() => {
      start.Wait();
      for (int index = 0; index < 128; index++)
        Assert.True(writer.LlmResponses.Append(session, run, 0, new(request, output, index, "x", false)));
    });
    start.Set();
    int reads = 0;
    while (!appends.IsCompleted || reads < 50) {
      TranscriptPositionWindow window = reader.Positions.ActivatePrompt(session, 0, prompt);
      Assert.Equal(prompt, window.Target!.Entry);
      AssertPrefixes(window);
      TranscriptPositionedEntry? text = window.Entries.SingleOrDefault(entry => entry.Position.Entry.Kind == TranscriptEntryKind.Text);
      long chunks = text?.Descriptor.Text!.AvailableThrough.SegmentIndex ?? 0;
      Assert.Equal(chunks + 1, window.State.PositionVersion);
      if (text is not null)
        Assert.Equal(256 + chunks, text.Position.Weight);
      reads++;
    }
    await appends;
    Assert.Equal(129, State(reader, session).PositionVersion);
  }

  private static void AssertPrefixes(TranscriptPositionWindow window) {
    long prefix = 0;
    for (int index = 0; index < window.Entries.Count; index++) {
      TranscriptPosition position = window.Entries[index].Position;
      Assert.Equal(index, position.Rank);
      Assert.Equal(prefix, position.PrefixWeight);
      prefix += position.Weight;
    }
    Assert.Equal(prefix, window.State.TotalWeight);
    Assert.Equal(window.Entries.Count, window.State.EntryCount);
  }

  private static TranscriptPositionState State(UrdStore store, Guid session) => store.Positions.Locate(session, 0, 0).State;
  private static UserMessageSubmission Input(string text) => new(Guid.NewGuid(), 0, text, TestLlmSelection.Default);
  private static void Admit(UrdStore store, Guid session, Guid run, UserMessageSubmission input) =>
      Assert.True(store.UserMessages.Admit(session, 0, input, true, run, run).NewlyAccepted);
  private static LlmToolCall Call(string id) => new(id, "named", JsonSerializer.SerializeToElement(new { }));
}
