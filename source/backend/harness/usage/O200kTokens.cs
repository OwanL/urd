using Microsoft.ML.Tokenizers;

namespace Urd.Core.Usage;

// OpenAI's o200k text BPE; other model families tokenize the same text within roughly 20%.
internal static class O200kTokens {
  private const int MaxCountedCharacters = 65_536; // Larger text scales this prefix's density to its full length.
  private static readonly Lazy<TiktokenTokenizer?> Tokenizer = new(Create);

  internal static TiktokenTokenizer? Instance => Tokenizer.Value;

  // Counts a text from its leading characters, so callers may hold only a bounded prefix.
  internal static long? Estimate(string prefix, long length) {
    ArgumentNullException.ThrowIfNull(prefix);
    if (length == 0) return 0;
    int counted = Math.Min(prefix.Length, MaxCountedCharacters);
    if (counted < prefix.Length && char.IsHighSurrogate(prefix[counted - 1]))
      counted--; // Matches a preview's retained prefix, which never ends mid-pair.
    if (counted == 0 || Instance is not { } tokenizer) return null;
    try {
      long tokens = tokenizer.CountTokens(prefix.AsSpan(0, counted));
      return counted == length ? tokens : (long)Math.Ceiling(tokens * (double)length / counted);
    } catch (Exception exception) when (exception is ArgumentException or InvalidOperationException) {
      return null;
    }
  }

  private static TiktokenTokenizer? Create() {
    try {
      // Text BPE only; provider framing, cache decisions and hidden reasoning remain provisional.
      return TiktokenTokenizer.CreateForModel("gpt-4o");
    } catch (Exception exception) when (exception is ArgumentException or InvalidOperationException
        or NotSupportedException) {
      return null;
    }
  }
}
