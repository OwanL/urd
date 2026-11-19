using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Urd.Backend.Tests.Support;
using Urd.Core.Context;
using Urd.Core.Execution;
using Urd.Core.Preferences;
using Urd.Core.Providers;
using Urd.Core.Sessions;
using Urd.Core.Usage;
using Xunit;
using static Urd.Backend.Tests.Support.ScriptedResponses;
using static Urd.Backend.Tests.Support.TestAsync;

namespace Urd.Backend.Tests.Context;

public sealed class StartingResourcePrunerTests {
  [Fact]
  public async Task SuccessfulPruningRetainsOriginalsInCandidateOrderAndRecordsCounts() {
    string taskInput = "Implement the selected feature.";
    DiscoveredSkill first = Skill("first", "First skill.");
    DiscoveredSkill second = Skill("second", "Second skill.");
    DiscoveredSkill protectedSkill = Skill("protected-secret", "Must not be disclosed.");
    string answer = JsonSerializer.Serialize(new { keptIds = new[] { "s2" } });
    var statuses = new ConcurrentQueue<StartingResourcePruningStatus>();
    var client = new ScriptedChatClient(token => TextUpdates([answer], token));
    await using RunningParent parent = await StartParentAsync();
    var runner = new ConversationRunner(parent.Fixture.Session.Recorder, _ => client, [], parent.Fixture.Session.WorkingDirectory);
    int resolutions = 0;
    var pruner = new StartingResourcePruner(runner, (purpose, _) => {
      Assert.Equal(AuxiliaryModelPurpose.Skills, purpose);
      Interlocked.Increment(ref resolutions);
      return Task.FromResult(TestLlmSelection.DefaultResolution);
    }, status => {
      statuses.Enqueue(status);
      return Task.CompletedTask;
    }, taskInput);

    StartingResourcePruningResult<DiscoveredSkill> result =
        await pruner.PruneAsync([first, second], protectedCount: 1, CancellationToken.None).WaitAsync(TestTimeout);

    Assert.False(result.IsFallback);
    Assert.Null(result.Error);
    Assert.Same(second, Assert.Single(result.Retained));
    Assert.Equal(1, resolutions);
    Assert.Contains(taskInput, client.LastRequest[^1].Text, StringComparison.Ordinal);
    Assert.Contains("runs in Urd", client.LastRequest[0].Text, StringComparison.Ordinal);
    using JsonDocument skillCandidates = ParseCandidates(client.LastRequest);
    JsonElement group = Assert.Single(skillCandidates.RootElement.EnumerateArray());
    Assert.Equal(Path.GetDirectoryName(second.Path), group.GetProperty("Directory").GetString());
    JsonElement[] files = group.GetProperty("Files").EnumerateArray().ToArray();
    Assert.Equal(new[] { "s1", "s2" }, files.Select(file => file.GetProperty("Id").GetString()));
    Assert.Equal(Path.GetFileName(second.Path), files[1].GetProperty("File").GetString());
    Assert.Equal(second.Name, files[1].GetProperty("Name").GetString());
    Assert.Equal(second.Description, files[1].GetProperty("Description").GetString());
    Assert.DoesNotContain(protectedSkill.Name, client.LastRequest[^1].Text, StringComparison.Ordinal);
    Assert.Equal(new[] { ("skills", "running", 3, 3), ("skills", "completed", 3, 2) },
        statuses.Select(status => (status.Category, status.State, status.Total, status.Retained)));

    Assert.Equal("pruning.skills", Assert.IsType<UsageContext>(
        client.Options.Single()!.AdditionalProperties![UsageContext.PropertyName]).Purpose);
  }

  [Fact]
  public async Task RepeatedSkillMetadataRetainsDistinctSelectableFiles() {
    const string sharedDescription = "Repeated skill metadata across several installations.";
    DiscoveredSkill first = Skill("first", sharedDescription) with { Name = "shared" };
    DiscoveredSkill second = Skill("second", sharedDescription) with { Name = "shared" };
    DiscoveredSkill different = Skill("different", "Different guidance.") with { Name = "shared" };
    string answer = JsonSerializer.Serialize(new { keptIds = new[] { "s3", "s2" } });
    var client = new ScriptedChatClient(token => TextUpdates([answer], token));
    await using RunningParent parent = await StartParentAsync();
    var runner = new ConversationRunner(parent.Fixture.Session.Recorder, _ => client, [], parent.Fixture.Session.WorkingDirectory);
    var pruner = CreatePruner(runner, _ => Task.CompletedTask);

    var result = await pruner.PruneAsync([first, different, second], 0, CancellationToken.None).WaitAsync(TestTimeout);

    Assert.False(result.IsFallback);
    Assert.Equal(new[] { different, second }, result.Retained);
    using JsonDocument candidates = ParseCandidates(client.LastRequest);
    JsonElement group = Assert.Single(candidates.RootElement.EnumerateArray());
    Assert.Equal(Path.GetDirectoryName(first.Path), group.GetProperty("Directory").GetString());
    JsonElement[] files = group.GetProperty("Files").EnumerateArray().ToArray();
    Assert.Equal(new[] { "s1", "s2", "s3" }, files.Select(file => file.GetProperty("Id").GetString()));
    Assert.Equal(new[] { first, different, second }.Select(skill => Path.GetFileName(skill.Path)),
        files.Select(file => file.GetProperty("File").GetString()));
    Assert.Equal(new[] { first, different, second }.Select(skill => skill.Description),
        files.Select(file => file.GetProperty("Description").GetString()));
  }

  [Fact]
  public async Task UnknownAndDuplicateIdsAreIgnoredAndMalformedResponsesFallBackWithoutRetrying() {
    DiscoveredSkill candidate = Skill("candidate", "Candidate.");
    string[] answers = ["{\"keptIds\":[\"unknown\"]}", "{\"keptIds\":[\"unknown\",\"s1\",\"s1\"]}", "not json"];
    int response = -1;
    var client = new ScriptedChatClient(token => TextUpdates([answers[Interlocked.Increment(ref response)]], token));
    await using RunningParent parent = await StartParentAsync();
    var runner = new ConversationRunner(parent.Fixture.Session.Recorder, _ => client, [], parent.Fixture.Session.WorkingDirectory);
    var pruner = CreatePruner(runner, _ => Task.CompletedTask);

    for (int index = 0; index < answers.Length; index++) {
      StartingResourcePruningResult<DiscoveredSkill> result =
          await pruner.PruneAsync([candidate], 0, CancellationToken.None).WaitAsync(TestTimeout);
      Assert.Equal(index == 2, result.IsFallback);
      if (index == 1)
        Assert.Same(candidate, Assert.Single(result.Retained));
      else
        Assert.Empty(result.Retained);
      if (index == 2)
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
      else
        Assert.Null(result.Error);
    }

    Assert.Equal(answers.Length, client.Calls);
  }

  [Fact]
  public async Task ToolCandidatesIncludeTheirTypedSchemaAndUseTheToolsPurpose() {
    AIFunctionDeclaration tool = AIFunctionFactory.CreateDeclaration("candidate-tool", "Tool description",
        JsonSerializer.SerializeToElement(new { type = "object", properties = new { path = new { type = "string" } } }));
    var client = new ScriptedChatClient(token => TextUpdates(["{\"keptIds\":[\"candidate-tool\"]}"], token));
    await using RunningParent parent = await StartParentAsync();
    var runner = new ConversationRunner(parent.Fixture.Session.Recorder, _ => client, [], parent.Fixture.Session.WorkingDirectory);
    var pruner = CreatePruner(runner, _ => Task.CompletedTask);

    StartingResourcePruningResult<AIFunctionDeclaration> result =
        await pruner.PruneAsync([tool], 0, CancellationToken.None).WaitAsync(TestTimeout);

    Assert.Same(tool, Assert.Single(result.Retained));
    Assert.Equal("pruning.tools", Assert.IsType<UsageContext>(
        client.Options.Single()!.AdditionalProperties![UsageContext.PropertyName]).Purpose);
    Assert.Contains("Tool description", client.LastRequest[^1].Text, StringComparison.Ordinal);
    Assert.Contains("candidate-tool", client.LastRequest[^1].Text, StringComparison.Ordinal);
    Assert.Contains("path", client.LastRequest[^1].Text, StringComparison.Ordinal);
  }

  [Fact]
  public async Task EmptyCategorySkipsModelResolutionAndProvider() {
    var statuses = new ConcurrentQueue<StartingResourcePruningStatus>();
    var client = new ScriptedChatClient(token => TextUpdates(["unused"], token));
    await using RunningParent parent = await StartParentAsync();
    var runner = new ConversationRunner(parent.Fixture.Session.Recorder, _ => client, [], parent.Fixture.Session.WorkingDirectory);
    var pruner = new StartingResourcePruner(runner, (_, _) => throw new InvalidOperationException("Must not resolve."),
        status => {
          statuses.Enqueue(status);
          return Task.CompletedTask;
        }, "task");

    StartingResourcePruningResult<DiscoveredSkill> result =
        await pruner.PruneAsync(Array.Empty<DiscoveredSkill>(), protectedCount: 2, CancellationToken.None)
            .WaitAsync(TestTimeout);

    Assert.Empty(result.Retained);
    Assert.False(result.IsFallback);
    Assert.Equal(0, client.Calls);
    Assert.Equal(new[] { ("running", 2, 2), ("completed", 2, 2) },
        statuses.Select(status => (status.State, status.Total, status.Retained)));
  }

  [Fact]
  public async Task StatusWriteFailureIsFatalAndNeverFallsBack() {
    var expected = new InvalidOperationException("status write failed");
    var client = new ScriptedChatClient(token => TextUpdates(["unused"], token));
    await using RunningParent parent = await StartParentAsync();
    var runner = new ConversationRunner(parent.Fixture.Session.Recorder, _ => client, [], parent.Fixture.Session.WorkingDirectory);
    var pruner = new StartingResourcePruner(runner, (_, _) => Task.FromResult(TestLlmSelection.DefaultResolution),
        _ => Task.FromException(expected), "task");

    InvalidOperationException actual = await Assert.ThrowsAsync<InvalidOperationException>(() =>
        pruner.PruneAsync([Skill("candidate", "Candidate.")], 0, CancellationToken.None).WaitAsync(TestTimeout));

    Assert.Same(expected, actual);
    Assert.Equal(0, client.Calls);
  }

  [Fact]
  public async Task AuxiliaryRecordingFailureIsFatalAndNeverFallsBack() {
    var client = new ScriptedChatClient(token => TextUpdates(["{\"keptIds\":[]}"], token));
    await using RunningParent parent = await StartParentAsync(expectFencedExecution: true);
    parent.Fixture.Database.Execute("""
      CREATE TRIGGER reject_auxiliary_output BEFORE INSERT ON auxiliary_output
      BEGIN SELECT RAISE(ABORT, 'Injected auxiliary output failure'); END;
      """);
    var statuses = new ConcurrentQueue<StartingResourcePruningStatus>();
    var runner = new ConversationRunner(parent.Fixture.Session.Recorder, _ => client, [], parent.Fixture.Session.WorkingDirectory);
    var pruner = new StartingResourcePruner(runner, (_, _) => Task.FromResult(TestLlmSelection.DefaultResolution), status => {
      statuses.Enqueue(status);
      return Task.CompletedTask;
    }, "task");

    await Assert.ThrowsAsync<AuxiliaryExecutionException>(() =>
        pruner.PruneAsync([Skill("candidate", "Candidate.")], 0, CancellationToken.None).WaitAsync(TestTimeout));

    Assert.Equal(new[] { "running" }, statuses.Select(status => status.State));
  }

  [Fact]
  public async Task CancellationWritesTerminalStatusAndPropagates() {
    var client = new ScriptedChatClient(token => TextUpdates(["unused"], token));
    await using RunningParent parent = await StartParentAsync();
    var runner = new ConversationRunner(parent.Fixture.Session.Recorder, _ => client, [], parent.Fixture.Session.WorkingDirectory);
    var statuses = new ConcurrentQueue<StartingResourcePruningStatus>();
    var pruner = new StartingResourcePruner(runner, (_, _) => Task.FromResult(TestLlmSelection.DefaultResolution), status => {
      statuses.Enqueue(status);
      return Task.CompletedTask;
    }, "task");
    using var cancellation = new CancellationTokenSource();
    await cancellation.CancelAsync();

    await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
        pruner.PruneAsync([Skill("candidate", "Candidate.")], protectedCount: 1, cancellation.Token).WaitAsync(TestTimeout));

    Assert.Equal(new[] { ("cancelled", 2, 1) }, statuses.Select(status => (status.State, status.Total, status.Retained)));
    Assert.Equal(0, client.Calls);
  }

  [Fact]
  public async Task InstructionOverflowDropsLargestUtf8BodiesWithStableTiesAndDoubles() {
    DiscoveredInstructionDocument[] documents = [
      Instruction("short", "12345"),
      Instruction("tie-first", "ééé"),
      Instruction("tie-second", "abcdef"),
      Instruction("medium", "22"),
      Instruction("last", "1")
    ];
    int attempt = 0;
    var client = new ScriptedChatClient(token => {
      if (Interlocked.Increment(ref attempt) <= 2)
        throw new ProviderOperationException(ProviderFailure.ContextWindowExceeded);
      return TextUpdates([JsonSerializer.Serialize(new { keptIds = new[] { "i5", "i4" } })], token);
    });
    await using RunningParent parent = await StartParentAsync();
    var runner = new ConversationRunner(parent.Fixture.Session.Recorder, _ => client, [], parent.Fixture.Session.WorkingDirectory);
    var statuses = new ConcurrentQueue<StartingResourcePruningStatus>();
    var pruner = CreatePruner(runner, status => { statuses.Enqueue(status); return Task.CompletedTask; });

    StartingResourcePruningResult<DiscoveredInstructionDocument> result =
        await pruner.PruneAsync(documents, protectedCount: 2, CancellationToken.None).WaitAsync(TestTimeout);

    Assert.False(result.IsFallback);
    Assert.Equal(new[] { documents[3], documents[4] }, result.Retained);
    Assert.Equal(3, client.Calls);
    Assert.Contains("runs in Urd", client.Requests[0][0].Text, StringComparison.Ordinal);
    using JsonDocument instructionCandidates = ParseCandidates(client.Requests[0]);
    JsonElement group = Assert.Single(instructionCandidates.RootElement.EnumerateArray());
    Assert.Equal(Path.GetDirectoryName(documents[0].Path), group.GetProperty("Directory").GetString());
    JsonElement[] files = group.GetProperty("Files").EnumerateArray().ToArray();
    Assert.Equal(Path.GetFileName(documents[0].Path), files[0].GetProperty("File").GetString());
    Assert.Equal(documents[0].Text, files[0].GetProperty("Content").GetString());
    Assert.Equal(documents[0].Scope, files[0].GetProperty("Scope").GetString());
    Assert.Equal(new[] { "i1", "i2", "i3", "i4", "i5" }, CandidateIds(client.Requests[0]));
    Assert.Equal(new[] { "i1", "i3", "i4", "i5" }, CandidateIds(client.Requests[1]));
    Assert.Equal(new[] { "i4", "i5" }, CandidateIds(client.Requests[2]));
    Assert.Contains("Skipped 1 instruction file", statuses.ElementAt(1).Error, StringComparison.Ordinal);
    Assert.Contains("Skipped 3 instruction files", statuses.Last().Error, StringComparison.Ordinal);
    Assert.Equal(new[] { ("running", 7, 7), ("running", 7, 6), ("running", 7, 4), ("completed", 7, 4) },
        statuses.Select(status => (status.State, status.Total, status.Retained)));
  }

  [Fact]
  public async Task InstructionOverflowAlwaysTriesLastCandidateThenFallsBackToProtectedOnly() {
    DiscoveredInstructionDocument[] documents = [Instruction("one", "1"), Instruction("two", "22"), Instruction("three", "333")];
    var client = new ScriptedChatClient(_ => throw new ProviderOperationException(ProviderFailure.ContextWindowExceeded));
    await using RunningParent parent = await StartParentAsync();
    var runner = new ConversationRunner(parent.Fixture.Session.Recorder, _ => client, [], parent.Fixture.Session.WorkingDirectory);
    var statuses = new ConcurrentQueue<StartingResourcePruningStatus>();
    var pruner = CreatePruner(runner, status => { statuses.Enqueue(status); return Task.CompletedTask; });

    StartingResourcePruningResult<DiscoveredInstructionDocument> result =
        await pruner.PruneAsync(documents, protectedCount: 1, CancellationToken.None).WaitAsync(TestTimeout);

    Assert.True(result.IsFallback);
    Assert.NotNull(result.Error);
    Assert.Empty(result.Retained);
    Assert.Equal(3, client.Calls);
    Assert.Equal(new[] { "i1" }, CandidateIds(client.Requests[2]));
    Assert.Contains("Skipped 3 instruction files", result.Error, StringComparison.Ordinal);
    Assert.Equal(new[] { ("running", 4, 4), ("running", 4, 3), ("running", 4, 2), ("fallback", 4, 1) },
        statuses.Select(status => (status.State, status.Total, status.Retained)));
  }

  [Fact]
  public async Task UnrelatedFailureAfterInstructionOverflowKeepsOnlyProtected() {
    DiscoveredInstructionDocument[] documents = [Instruction("small", "1"), Instruction("large", "12345")];
    int attempt = 0;
    var client = new ScriptedChatClient(_ => throw new ProviderOperationException(
        Interlocked.Increment(ref attempt) == 1 ? ProviderFailure.ContextWindowExceeded : ProviderFailure.ProviderUnavailable));
    await using RunningParent parent = await StartParentAsync();
    var runner = new ConversationRunner(parent.Fixture.Session.Recorder, _ => client, [], parent.Fixture.Session.WorkingDirectory);
    var pruner = CreatePruner(runner, _ => Task.CompletedTask);

    StartingResourcePruningResult<DiscoveredInstructionDocument> result =
        await pruner.PruneAsync(documents, protectedCount: 1, CancellationToken.None).WaitAsync(TestTimeout);

    Assert.True(result.IsFallback);
    Assert.Contains("Skipped 1 instruction file", result.Error, StringComparison.Ordinal);
    Assert.Contains("ProviderUnavailable", result.Error, StringComparison.Ordinal);
    Assert.Empty(result.Retained);
    Assert.Equal(2, client.Calls);
    Assert.Equal(new[] { "i1" }, CandidateIds(client.Requests[1]));
  }

  [Fact]
  public async Task SkillContextOverflowDoesNotRetryAndExcludesCandidates() {
    DiscoveredSkill[] skills = [Skill("first-overflow", "First"), Skill("second-overflow", "Second")];
    var client = new ScriptedChatClient(_ => throw new ProviderOperationException(ProviderFailure.ContextWindowExceeded));
    await using RunningParent parent = await StartParentAsync();
    var runner = new ConversationRunner(parent.Fixture.Session.Recorder, _ => client, [], parent.Fixture.Session.WorkingDirectory);
    var pruner = CreatePruner(runner, _ => Task.CompletedTask);

    StartingResourcePruningResult<DiscoveredSkill> result =
        await pruner.PruneAsync(skills, protectedCount: 1, CancellationToken.None).WaitAsync(TestTimeout);

    Assert.True(result.IsFallback);
    Assert.Empty(result.Retained);
    Assert.Equal(1, client.Calls);
  }

  [Fact]
  public async Task ToolFailureRetainsAllCandidates() {
    AIFunctionDeclaration tool = AIFunctionFactory.CreateDeclaration("candidate-tool", "Tool description",
        JsonSerializer.SerializeToElement(new { type = "object" }));
    var client = new ScriptedChatClient(_ => throw new ProviderOperationException(ProviderFailure.ProviderUnavailable));
    await using RunningParent parent = await StartParentAsync();
    var runner = new ConversationRunner(parent.Fixture.Session.Recorder, _ => client, [], parent.Fixture.Session.WorkingDirectory);
    var pruner = CreatePruner(runner, _ => Task.CompletedTask);

    var result = await pruner.PruneAsync([tool], 1, CancellationToken.None).WaitAsync(TestTimeout);

    Assert.True(result.IsFallback);
    Assert.Same(tool, Assert.Single(result.Retained));
    Assert.Contains("Retained all unprotected tools", result.Error, StringComparison.Ordinal);
  }

  [Fact]
  public async Task IncompleteIndexWarningComposesWithSkippedFilesOnInstructions() {
    const string indexWarning = "Index of C:\\ incomplete: 1,240 files found so far.";
    DiscoveredInstructionDocument[] documents = [Instruction("large", "12345"), Instruction("small", "1")];
    int attempt = 0;
    var client = new ScriptedChatClient(token => {
      if (Interlocked.Increment(ref attempt) == 1)
        throw new ProviderOperationException(ProviderFailure.ContextWindowExceeded);
      return TextUpdates([JsonSerializer.Serialize(new { keptIds = new[] { "i2" } })], token);
    });
    await using RunningParent parent = await StartParentAsync();
    var runner = new ConversationRunner(parent.Fixture.Session.Recorder, _ => client, [], parent.Fixture.Session.WorkingDirectory);
    var statuses = new ConcurrentQueue<StartingResourcePruningStatus>();
    var pruner = CreatePruner(runner, status => { statuses.Enqueue(status); return Task.CompletedTask; });

    StartingResourcePruningResult<DiscoveredInstructionDocument> result =
        await pruner.PruneAsync(documents, 0, CancellationToken.None, indexWarning).WaitAsync(TestTimeout);

    Assert.False(result.IsFallback);
    Assert.Equal(new[] { documents[1] }, result.Retained);
    const string combined =
        "Index of C:\\ incomplete: 1,240 files found so far. Skipped 1 instruction file because the context limit was exceeded.";
    Assert.Equal(new[] {
      (State: "running", Error: (string?)indexWarning),
      (State: "running", Error: (string?)combined),
      (State: "completed", Error: (string?)combined),
    }, statuses.Select(status => (status.State, status.Error)).ToArray());
  }

  [Fact]
  public async Task RefreshingIndexWarningTravelsWithSkillStatuses() {
    const string indexWarning = "Index of C:\\ is being refreshed; results may be stale.";
    DiscoveredSkill first = Skill("first", "First skill.");
    DiscoveredSkill second = Skill("second", "Second skill.");
    string answer = JsonSerializer.Serialize(new { keptIds = new[] { "s1" } });
    var client = new ScriptedChatClient(token => TextUpdates([answer], token));
    await using RunningParent parent = await StartParentAsync();
    var runner = new ConversationRunner(parent.Fixture.Session.Recorder, _ => client, [], parent.Fixture.Session.WorkingDirectory);
    var statuses = new ConcurrentQueue<StartingResourcePruningStatus>();
    var pruner = CreatePruner(runner, status => { statuses.Enqueue(status); return Task.CompletedTask; });

    StartingResourcePruningResult<DiscoveredSkill> result =
        await pruner.PruneAsync([first, second], 0, CancellationToken.None, indexWarning).WaitAsync(TestTimeout);

    Assert.False(result.IsFallback);
    Assert.Same(first, Assert.Single(result.Retained));
    Assert.Equal(new[] { (State: "running", Error: (string?)indexWarning), (State: "completed", Error: (string?)indexWarning) },
        statuses.Select(status => (status.State, status.Error)).ToArray());
  }

  private static string[] CandidateIds(IReadOnlyList<ChatMessage> request) {
    using JsonDocument json = ParseCandidates(request);
    return json.RootElement.EnumerateArray().SelectMany(group => group.GetProperty("Files").EnumerateArray())
        .Select(candidate => candidate.GetProperty("Id").GetString()!).ToArray();
  }

  private static JsonDocument ParseCandidates(IReadOnlyList<ChatMessage> request) {
    string text = request[^1].Text;
    int start = text.IndexOf("Candidates:", StringComparison.Ordinal);
    Assert.True(start >= 0);
    return JsonDocument.Parse(text[(start + "Candidates:".Length)..]);
  }

  private static DiscoveredInstructionDocument Instruction(string name, string text) =>
      new(text, Path.Combine(Path.GetTempPath(), $"{name}.instructions.md"), Path.GetTempPath());

  private static StartingResourcePruner CreatePruner(
      ConversationRunner runner,
      Func<StartingResourcePruningStatus, Task> saveStatus) =>
      new(runner, (_, _) => Task.FromResult(TestLlmSelection.DefaultResolution), saveStatus, "Choose relevant resources.");

  private static DiscoveredSkill Skill(string name, string description) =>
      new(name, description, Path.GetFullPath(Path.Combine(Path.GetTempPath(), $"{name}.SKILL.md")));

  private static async Task<RunningParent> StartParentAsync(bool expectFencedExecution = false) {
    var mainWaiting = Signal();
    var mainRelease = Signal();
    var main = new ScriptedChatClient(token => Paused(mainWaiting, mainRelease, token));
    var fixture = new ExecutionFixture(main) { ExpectFencedExecution = expectFencedExecution };
    Guid executionId = await fixture.StartExecutionAsync("main input");
    await mainWaiting.Task.WaitAsync(TestTimeout);
    return new(fixture, executionId, mainRelease);
  }

  private sealed class RunningParent(ExecutionFixture fixture, Guid executionId, TaskCompletionSource mainRelease)
      : IAsyncDisposable {
    internal ExecutionFixture Fixture => fixture;

    public async ValueTask DisposeAsync() {
      mainRelease.TrySetResult();
      if (fixture.ExpectFencedExecution)
        await Assert.ThrowsAsync<AggregateException>(() => fixture.Session.WaitForExecutionFinishAsync(executionId).WaitAsync(TestTimeout));
      else
        await fixture.Session.WaitForExecutionFinishAsync(executionId).WaitAsync(TestTimeout);
      await fixture.DisposeAsync();
    }
  }
}
