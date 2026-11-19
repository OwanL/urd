using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Urd.Backend.Tests.Support;
using Urd.Core.Execution;
using Urd.Core.Preferences;
using Urd.Core.Providers;
using Urd.Core.Sessions;
using Urd.Core.Storage;
using Xunit;
using Xunit.Abstractions;
using static Urd.Backend.Tests.Support.ScriptedResponses;
using static Urd.Backend.Tests.Support.TestAsync;

namespace Urd.Backend.Tests.Sessions;

public sealed class InstructionResourceIntegrationTests(ITestOutputHelper output) {
  [Theory]
  [InlineData(true)]
  [InlineData(false)]
  public async Task FirstChatPreparesLargeInstructionCatalog(bool overflowSelector) {
    const int count = 1900;
    const int outlierIndex = 0;
    const int outlierPaddingBytes = 256 * 1024;
    const string launch = "Apply the project instructions.";
    string selectedPath = "";
    string outlierPath = "";
    int outlierBytes = 0;
    int? selectorThreshold = null;
    var selectors = new List<ScriptedRequest>();
    ScriptedRequest? main = null;
    await using var fixture = new ExecutionFixture(new ScriptedChatClient(Route), [],
        store => new(store, () => throw new InvalidOperationException("No callback expected."),
            new ScriptedOpenAIHandler("{}", "application/json")));
    string project = Path.Combine(fixture.Session.WorkingDirectory!, "project");
    var expected = new Dictionary<string, string>(StringComparer.Ordinal);
    for (int index = 0; index < count; index++) {
      string directory = Path.Combine(project, $"part-{index:D4}");
      Directory.CreateDirectory(directory);
      string path = Path.Combine(directory, "AGENTS.md");
      string content = $"Instruction marker {index:D4}.\n{new string('x', index % 97)}";
      if (index == outlierIndex)
        content += new string('z', outlierPaddingBytes);
      await File.WriteAllTextAsync(path, content);
      expected.Add(path, content);
    }

    selectedPath = Path.Combine(project, "part-0950", "AGENTS.md");
    outlierPath = Path.Combine(project, $"part-{outlierIndex:D4}", "AGENTS.md");
    outlierBytes = Encoding.UTF8.GetByteCount(expected[outlierPath]);
    Assert.True(outlierBytes > expected.Where(pair => pair.Key != outlierPath)
        .Max(pair => Encoding.UTF8.GetByteCount(pair.Value)));
    Guid execution = await fixture.StartExecutionAsync(launch);
    SessionExecutionResult result = await fixture.Session.WaitForExecutionFinishAsync(execution).WaitAsync(TimeSpan.FromMinutes(2));
    Assert.Equal(ExecutionOutcome.Completed, result.Outcome);

    Assert.NotEmpty(selectors);
    var actual = ReadCandidates(selectors[0]);
    Assert.Equal(expected.OrderBy(pair => pair.Key), actual.OrderBy(pair => pair.Key));
    output.WriteLine($"Selector user message: {Encoding.UTF8.GetByteCount(selectors[0].LastUserText)} UTF-8 bytes for {actual.Count} documents.");
    if (overflowSelector) {
      Assert.Equal(2, selectors.Count);
      int firstBytes = Encoding.UTF8.GetByteCount(selectors[0].LastUserText);
      int retryBytes = Encoding.UTF8.GetByteCount(selectors[1].LastUserText);
      int threshold = Assert.IsType<int>(selectorThreshold);
      var retryCandidates = ReadCandidates(selectors[1]);
      var expectedRetry = expected.Where(pair => pair.Key != outlierPath).ToDictionary(pair => pair.Key, pair => pair.Value,
          StringComparer.Ordinal);
      Assert.Equal(expectedRetry.OrderBy(pair => pair.Key), retryCandidates.OrderBy(pair => pair.Key));
      var firstEntries = ReadCandidateEntries(selectors[0]);
      var retryEntries = ReadCandidateEntries(selectors[1]);
      Assert.All(retryEntries, pair => Assert.Equal(firstEntries[pair.Key].Id, pair.Value.Id));
      Assert.True(firstBytes > threshold);
      Assert.True(retryBytes <= threshold);
      Assert.True(firstBytes - retryBytes >= outlierBytes);
      output.WriteLine($"Selector context overflow retry: {retryBytes} UTF-8 bytes (threshold {threshold}) for {retryCandidates.Count} documents; dropped largest document ({outlierBytes} UTF-8 bytes).");
    } else {
      Assert.Single(selectors);
    }

    Assert.NotNull(main);
    Assert.Equal(launch, main.LastUserText);
    string[] system = main.Messages.Where(message => message.Role == ChatRole.System).Select(message => message.Text).ToArray();
    string[] retained = system.SelectMany(text => text.Split('\n')).Select(line => line.TrimEnd('\r'))
        .Where(line => line.StartsWith("Instruction marker ", StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToArray();
    string[] expectedRetained = overflowSelector
        ? expected.Where(pair => pair.Key != outlierPath).Select(pair => pair.Value.Split('\n')[0]).Order(StringComparer.Ordinal).ToArray()
        : [expected[selectedPath].Split('\n')[0]];
    Assert.Equal(expectedRetained, retained);
    output.WriteLine($"Main system messages: {system.Sum(Encoding.UTF8.GetByteCount)} UTF-8 bytes.");
    var status = Assert.Single(fixture.Store.Snapshots.ReadPersisted(fixture.Session.Id).Pruning!,
        item => item.Category == "instructions");
    Assert.Equal("completed", status.State);
    Assert.Equal(count, status.Total);
    Assert.Equal(retained.Length, status.Retained);
    if (overflowSelector)
      Assert.Contains("Skipped 1 instruction file", status.Error, StringComparison.Ordinal);
    else
      Assert.Null(status.Error);

    IAsyncEnumerable<ChatResponseUpdate> Route(ScriptedRequest request, CancellationToken token) {
      switch (request.Purpose) {
        case "pruning.instructions": {
          selectors.Add(request);
          string[] candidateIds = ReadCandidateIds(request);
          if (overflowSelector) {
            int requestBytes = Encoding.UTF8.GetByteCount(request.LastUserText);
            selectorThreshold ??= requestBytes - outlierBytes / 2;
            if (requestBytes > selectorThreshold.Value)
              throw new ProviderOperationException(ProviderFailure.ContextWindowExceeded);
            return TextUpdates([JsonSerializer.Serialize(new { keptIds = candidateIds })], token);
          }
          return TextUpdates([JsonSerializer.Serialize(new { keptIds = new[] { ReadCandidateEntries(request)[selectedPath].Id } })], token);
        }
        case "pruning.skills":
        case "pruning.tools":
          return TextUpdates(["{\"keptIds\":[]}"], token);
        case "title":
          return TextUpdates(["Large catalog"], token);
        case "conversation":
          main = request;
          return TextUpdates(["done"], token);
        default:
          throw new InvalidOperationException($"Unexpected mock request purpose '{request.Purpose}'.");
      }
    }
  }

  private static string[] ReadCandidateIds(ScriptedRequest request) =>
      ReadCandidateEntries(request).Values.Select(value => value.Id).ToArray();

  private static Dictionary<string, string> ReadCandidates(ScriptedRequest request) =>
      ReadCandidateEntries(request).ToDictionary(pair => pair.Key, pair => pair.Value.Content, StringComparer.Ordinal);

  private static Dictionary<string, (string Id, string Content)> ReadCandidateEntries(ScriptedRequest request) {
    const string marker = "Candidates:\n";
    string prompt = request.LastUserText.ReplaceLineEndings("\n");
    int start = prompt.LastIndexOf(marker, StringComparison.Ordinal);
    Assert.True(start >= 0);
    using JsonDocument candidates = JsonDocument.Parse(prompt[(start + marker.Length)..]);
    return candidates.RootElement.EnumerateArray().SelectMany(group =>
        group.GetProperty("Files").EnumerateArray().Select(file => (
            Path: Path.Combine(group.GetProperty("Directory").GetString()!, file.GetProperty("File").GetString()!),
            Id: file.GetProperty("Id").GetString()!, Content: file.GetProperty("Content").GetString()!)))
        .ToDictionary(entry => entry.Path, entry => (entry.Id, entry.Content), StringComparer.Ordinal);
  }

  [Fact]
  public async Task PickerAndDirectSessionShareWarmedCatalog() {
    using var database = new TemporaryDatabase();
    string directory = Path.Combine(Path.GetDirectoryName(database.Path)!, "project");
    Directory.CreateDirectory(directory);
    string instruction = Path.Combine(directory, "AGENTS.md");
    await File.WriteAllTextAsync(instruction, "Integration instruction marker");
    using UrdStore store = UrdStore.Open(database.Path);
    new PreferenceManager(store).SetWorkingDirectory(directory);
    store.WarmInstructionResources(directory);
    using var client = new ScriptedChatClient(Complete);
    await using var manager = new SessionManager(store, _ => client);
    var picker = await manager.ReadStartingResourcesAsync();
    Assert.Contains(picker.Instructions, item => item.Id == instruction);

    var session = new PrimarySession(store.Sessions.CreatePrimary(Guid.NewGuid(), directory), store, _ => client);
    try {
      UserMessageReceipt receipt = await session.SubmitUserMessageAsync(
          new(Guid.NewGuid(), 0, "hello", TestLlmSelection.Default));
      await session.WaitForExecutionFinishAsync(receipt.ExecutionId!.Value).WaitAsync(TestTimeout);
      Assert.Contains(client.Requests[0], message => message.Role == ChatRole.System
          && message.Text.Contains("Integration instruction marker", StringComparison.Ordinal));
    } finally {
      await session.StopAcceptingUserMessagesAsync();
      await session.StopExecutionAsync();
    }
  }
}
