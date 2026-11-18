namespace Urd.Core.Tools;

// Bounds for settling, extracted document size and inline text.
internal static class WebReadLimits {
  public const int SettlePollMs = 250;
  public const int SettleAttempts = 12;
  public const long MaxInputBytes = 10 * 1024 * 1024;
  public const int InlineTextBytes = 8 * 1024;
  public const int PreviewTextBytes = 4 * 1024;
}
