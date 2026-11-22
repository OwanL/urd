using Microsoft.Extensions.AI;
using Urd.Backend.Tests.Support;
using Urd.Core.Execution;
using Xunit;
using static Urd.Backend.Tests.Support.ScriptedResponses;
using static Urd.Backend.Tests.Support.TestAsync;

namespace Urd.Backend.Tests.Execution;

// Titles count Unicode scalar values, and generation corrects exactly one rejected answer.
public sealed class TitleGeneratorTests {
  [Fact]
  public void ValidityCountsUnicodeScalarsUpToTwentyFiveAndRejectsControlOrWhitespace() {
    Assert.Equal(25, TitleGenerator.MaximumCharacters);
    Assert.True(TitleGenerator.IsValid(new string('a', 25)));
    Assert.True(TitleGenerator.IsValid("🧭" + new string('a', 24))); // 25 runes in 26 UTF-16 units
    Assert.False(TitleGenerator.IsValid(new string('a', 26)));
    Assert.False(TitleGenerator.IsValid("🧭" + new string('a', 25))); // 26 runes
    Assert.False(TitleGenerator.IsValid(" orbit "));
    Assert.False(TitleGenerator.IsValid("a\tb"));
    Assert.False(TitleGenerator.IsValid(""));
    Assert.False(TitleGenerator.IsValid("  "));
  }

  [Fact]
  public void FallbackCollapsesWhitespaceAndControlCharactersAndKeepsTwentyFiveScalars() {
    Assert.Equal("first prompt here", TitleGenerator.CreateFallback("  first \r\n prompt \t here  "));
    Assert.Equal("a b c", TitleGenerator.CreateFallback("a\u0000b\u0007\nc\t\t "));
    Assert.Null(TitleGenerator.CreateFallback(" \u0007\t \u0001"));
    // Truncation counts 25 Unicode scalars and would never split a surrogate pair.
    Assert.Equal(new string('a', 24) + "🧭", TitleGenerator.CreateFallback(new string('a', 24) + "🧭🧭🧭"));
  }

  [Fact]
  public async Task GenerationCorrectsAnOverlongAnswerAndTrimsSurroundingLineEndings() {
    var mainWaiting = Signal();
    var mainRelease = Signal();
    var main = new ScriptedChatClient(token => Paused(mainWaiting, mainRelease, token));
    await using var fixture = new ExecutionFixture(main);
    Guid execution = await fixture.StartExecutionAsync("first main input");
    await mainWaiting.Task.WaitAsync(TestTimeout);

    int scriptedCalls = 2;
    // 26 scalar values exceed the limit, even though each needs two UTF-16 units.
    string tooLong = string.Concat(Enumerable.Repeat("\U0001F9ED", 26));
    string corrected = "Orbit corrected title";
    var client = new ScriptedChatClient(token => Script(token));
    var runner = new ConversationRunner(fixture.Session.Recorder, _ => client, [], fixture.Session.WorkingDirectory);

    string title = await TitleGenerator.GenerateAsync(runner, TestLlmSelection.DefaultResolution,
        "name this session", CancellationToken.None).WaitAsync(TestTimeout);

    Assert.Equal(corrected, title);
    Assert.Equal(2, client.Calls);
    Assert.Equal(0, client.DisposeCalls);
    ChatMessage correction = client.Requests[1][^1];
    Assert.Equal(ChatRole.User, correction.Role);
    Assert.Contains("Invalid title", correction.Text, StringComparison.Ordinal);

    try {
      mainRelease.TrySetResult();
      await fixture.Session.WaitForExecutionFinishAsync(execution).WaitAsync(TestTimeout);
    } finally {
      mainRelease.TrySetResult();
    }

    IAsyncEnumerable<ChatResponseUpdate> Script(CancellationToken token) =>
        --scriptedCalls == 0 ? TextUpdates([$"\r\n{corrected}\n"], token) : TextUpdates([tooLong], token);
  }
}
