using System.Text;
using Microsoft.Extensions.AI;
using Urd.Core.Context;
using Urd.Core.Providers;

namespace Urd.Core.Execution;

// Titles count Unicode scalar values, including spaces, rather than UTF-16 code units.
internal static class TitleGenerator {
  internal const int MaximumCharacters = TitleGuidance.MaximumCharacters;

  internal static bool IsValid(string title) => !string.IsNullOrWhiteSpace(title)
      && title == title.Trim() && !title.Any(char.IsControl)
      && title.EnumerateRunes().Count() <= MaximumCharacters;

  // A failed or cancelled generation attempt falls back to the first prompt. Whitespace and control
  // characters collapse into single spaces so the fallback keeps the published shape; an empty
  // result has no title and the attempt stays failed. Cancellation is settled by the caller with
  // this same publication.
  internal static string? CreateFallback(string prompt) {
    var collapsed = new StringBuilder(prompt.Length);
    bool pendingSpace = false;
    foreach (Rune rune in prompt.EnumerateRunes()) {
      if (Rune.IsWhiteSpace(rune) || Rune.IsControl(rune)) {
        pendingSpace = collapsed.Length > 0;
        continue;
      }
      if (pendingSpace) {
        collapsed.Append(' ');
        pendingSpace = false;
      }
      collapsed.Append(rune.ToString());
    }
    return collapsed.Length == 0 ? null : LimitScalars(collapsed.ToString(), MaximumCharacters);
  }

  // Keeps the leading Unicode scalars, dropping any partial trailing surrogate pair with them.
  internal static string LimitScalars(string value, int maximumScalars) {
    return value.EnumerateRunes().Skip(maximumScalars).Any()
        ? string.Concat(value.EnumerateRunes().Take(maximumScalars).Select(rune => rune.ToString()))
        : value;
  }

  internal static Task<string> GenerateAsync(ConversationRunner runner, AuxiliaryModelResolution model,
      string prompt, CancellationToken cancellation) => runner.RunAsync("title", model, [
        new(ChatRole.System, TitleGuidance.System),
        new(ChatRole.User, prompt)
      ], title => IsValid(title) ? null : TitleGuidance.Correction,
      cancellation);
}
