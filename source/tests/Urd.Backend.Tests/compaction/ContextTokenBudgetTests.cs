using System.Text.Json;
using Microsoft.Extensions.AI;
using Urd.Core.Compaction;
using Xunit;

namespace Urd.Backend.Tests.Compaction;

public sealed class ContextTokenBudgetTests {
  [Fact]
  public void EstimateTextRoundsUpAtFourCharacterBoundaries() {
    Assert.Equal(0, ContextTokenBudget.EstimateText(null));
    Assert.Equal(0, ContextTokenBudget.EstimateText(string.Empty));
    Assert.Equal(1, ContextTokenBudget.EstimateText("x"));
    Assert.Equal(1, ContextTokenBudget.EstimateText("xxxx"));
    Assert.Equal(2, ContextTokenBudget.EstimateText("xxxxx"));
    Assert.Equal(2, ContextTokenBudget.EstimateText("xxxxxxxx"));
  }

  [Fact]
  public void EstimateMessageIncludesTextRoleAndRawRepresentation() {
    var message = new ChatMessage(ChatRole.User, "xxxxx");
    var rawMessage = new ChatMessage(ChatRole.User, "xxxxx") { RawRepresentation = new object() };

    Assert.Equal(11, ContextTokenBudget.EstimateMessage(message));
    Assert.Equal(19, ContextTokenBudget.EstimateMessage(rawMessage));
    Assert.Equal(11, ContextTokenBudget.EstimateMessages([message]));
    Assert.Equal(0, ContextTokenBudget.EstimateMessages([]));
  }

  [Fact]
  public void EstimateMessageAccountsForStructuredAndProtectedContent() {
    var arguments = new Dictionary<string, object?> { ["value"] = "xxxxx" };
    AIContent[] nestedResult = [new TextContent("xxxxx"), new DataContent(new byte[] { 1, 2, 3 }, "image/png")];
    var message = new ChatMessage(ChatRole.Assistant, [
      new TextContent("xxxxx"),
      new TextReasoningContent("xxxxx"),
      new TextReasoningContent("xxxxx") { ProtectedData = "encrypted" },
      new DataContent(new byte[] { 1, 2, 3 }, "image/png"),
      new FunctionCallContent("call", "lookup", arguments),
      new FunctionResultContent("call", "xxxxx"),
      new FunctionResultContent("nested", nestedResult),
      new AIContent()
    ]);
    long expected = 8 + ContextTokenBudget.EstimateText("assistant")
        + ContextTokenBudget.EstimateText("xxxxx")
        + ContextTokenBudget.EstimateText("xxxxx")
        + 1_024 + 1_024 + ContextTokenBudget.EstimateText(JsonSerializer.Serialize(arguments))
        + ContextTokenBudget.EstimateText("xxxxx")
        + ContextTokenBudget.EstimateText("xxxxx") + 1_024 + 8;

    Assert.Equal(expected, ContextTokenBudget.EstimateMessage(message));
  }

  [Fact]
  public void BudgetsRespectWindowBoundariesAndFallbacks() {
    Assert.Equal(0, ContextTokenBudget.CompactionTrigger(1));
    Assert.Equal(4, ContextTokenBudget.CompactionTrigger(5));
    Assert.Equal(65_540, ContextTokenBudget.CompactionTrigger(81_924));
    Assert.Equal(65_541, ContextTokenBudget.CompactionTrigger(81_925));
    Assert.Equal(83_616, ContextTokenBudget.CompactionTrigger(100_000));
    Assert.Equal(int.MaxValue - 16_384, ContextTokenBudget.CompactionTrigger(int.MaxValue));
    Assert.Null(ContextTokenBudget.CompactionTrigger(null));
    Assert.Equal(10_000, ContextTokenBudget.CompactionTrigger(null, 10_000));
    Assert.Equal(10_000, ContextTokenBudget.CompactionTrigger(100_000, 10_000));
    Assert.Equal(83_616, ContextTokenBudget.CompactionTrigger(100_000, int.MaxValue));
    Assert.Throws<ArgumentOutOfRangeException>(() => ContextTokenBudget.CompactionTrigger(100_000, 0));
    Assert.Throws<ArgumentOutOfRangeException>(() => ContextTokenBudget.CompactionTrigger(null, -1));

    Assert.Equal(0, ContextTokenBudget.RetainedHistoryLimit(0));
    Assert.Equal(0, ContextTokenBudget.RetainedHistoryLimit(1));
    Assert.Equal(500, ContextTokenBudget.RetainedHistoryLimit(1_001));
    Assert.Equal(20_000, ContextTokenBudget.RetainedHistoryLimit(100_000));
    Assert.Equal(20_000, ContextTokenBudget.RetainedHistoryLimit(null));

    Assert.Equal(1, ContextTokenBudget.SummaryOutputLimit(1));
    Assert.Equal(250, ContextTokenBudget.SummaryOutputLimit(1_000));
    Assert.Equal(8_191, ContextTokenBudget.SummaryOutputLimit(32_764));
    Assert.Equal(8_192, ContextTokenBudget.SummaryOutputLimit(32_768));
    Assert.Equal(8_192, ContextTokenBudget.SummaryOutputLimit(null));
    Assert.Equal(8_192, ContextTokenBudget.SummaryOutputLimit(0));
  }

  [Fact]
  public void AddSaturatingCapsAtLongMaximum() {
    Assert.Equal(18, ContextTokenBudget.AddSaturating(7, 11));
    Assert.Equal(long.MaxValue, ContextTokenBudget.AddSaturating(long.MaxValue - 4, 5));
    Assert.Equal(long.MaxValue, ContextTokenBudget.AddSaturating(long.MaxValue, 0));
  }

  [Fact]
  public void PublicEstimatorsRejectNullMessagesAndCollections() {
    Assert.Throws<ArgumentNullException>(() => ContextTokenBudget.EstimateMessages(null!));
    Assert.Throws<ArgumentNullException>(() => ContextTokenBudget.EstimateMessage(null!));
  }
}
