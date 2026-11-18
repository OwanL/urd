using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Urd.Backend.Tests.Support;
using Urd.Core.Execution;
using Urd.Core.Preferences;
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
  public async Task FirstChatPreparesLargeInstructionCatalog(bool rejectSelector) {
    const int count = 1900;
    const string launch = "Apply the project instructions.";
    const string rejection = "400 Bad Request: maximum context length exceeded in instruction selector";
    string selectedPath = "";
    ScriptedRequest? selector = null;
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
      string content = $"Instruction marker {index:D4}.";
      await File.WriteAllTextAsync(path, content);
      expected.Add(path, content);
    }

    selectedPath = Path.Combine(project, "part-0950", "AGENTS.md");
    Guid execution = await fixture.StartExecutionAsync(launch);
    SessionExecutionResult result = await fixture.Session.WaitForExecutionFinishAsync(execution).WaitAsync(TimeSpan.FromMinutes(2));
    Assert.Equal(ExecutionOutcome.Completed, result.Outcome);

    Assert.NotNull(selector);
    const string marker = "Candidates:\n";
    string prompt = selector.LastUserText.ReplaceLineEndings("\n");
    int start = prompt.LastIndexOf(marker, StringComparison.Ordinal);
    Assert.True(start >= 0);
    using JsonDocument candidates = JsonDocument.Parse(prompt[(start + marker.Length)..]);
    var actual = candidates.RootElement.EnumerateArray().ToDictionary(entry => entry.GetProperty("Id").GetString()!,
        entry => entry.GetProperty("Content").GetString()!, StringComparer.Ordinal);
    Assert.Equal(expected.OrderBy(pair => pair.Key), actual.OrderBy(pair => pair.Key));
    output.WriteLine($"Selector user message: {Encoding.UTF8.GetByteCount(selector.LastUserText)} UTF-8 bytes for {actual.Count} documents.");

    Assert.NotNull(main);
    Assert.Equal(launch, main.LastUserText);
    string[] system = main.Messages.Where(message => message.Role == ChatRole.System).Select(message => message.Text).ToArray();
    string[] retained = system.SelectMany(text => text.Split('\n')).Select(line => line.TrimEnd('\r'))
        .Where(line => line.StartsWith("Instruction marker ", StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToArray();
    Assert.Equal(rejectSelector ? expected.Values.Order(StringComparer.Ordinal).ToArray() : [expected[selectedPath]], retained);
    output.WriteLine($"Main system messages: {system.Sum(Encoding.UTF8.GetByteCount)} UTF-8 bytes.");
    var status = Assert.Single(fixture.Store.Snapshots.ReadPersisted(fixture.Session.Id).Pruning!,
        item => item.Category == "instructions");
    Assert.Equal(rejectSelector ? "fallback" : "completed", status.State);
    Assert.Equal(count, status.Total);
    Assert.Equal(retained.Length, status.Retained);
    if (rejectSelector)
      Assert.Contains(rejection, status.Error, StringComparison.Ordinal);

    IAsyncEnumerable<ChatResponseUpdate> Route(ScriptedRequest request, CancellationToken token) {
      switch (request.Purpose) {
        case "pruning.instructions":
          selector = request;
          if (rejectSelector && Encoding.UTF8.GetByteCount(request.LastUserText) > 128 * 1024)
            throw new InvalidOperationException(rejection);
          return TextUpdates([JsonSerializer.Serialize(new { keptIds = new[] { selectedPath } })], token);
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
