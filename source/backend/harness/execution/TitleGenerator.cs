using System.Text;
using Microsoft.Extensions.AI;
using Urd.Core.Providers;

namespace Urd.Core.Execution;

// Titles count Unicode scalar values, including spaces, rather than UTF-16 code units.
internal static class TitleGenerator {
  internal const int MaximumCharacters = 25;

  internal static bool IsValid(string title) => !string.IsNullOrWhiteSpace(title)
      && title == title.Trim() && !title.Any(char.IsControl)
      && title.EnumerateRunes().Count() <= MaximumCharacters;

  internal static Task<string> GenerateAsync(ConversationRunner runner, LlmSelection selection,
      string prompt, CancellationToken cancellation) => runner.RunAsync("title", selection, [
        new(ChatRole.System, "Name this session from its first user message. You may independently explore with read/bash. "
            + "Return only a short plain-text title, at most 25 characters including spaces. No quotes, markup or explanation."),
        new(ChatRole.User, prompt)
      ], title => IsValid(title) ? null
          : "Invalid title. Return only a nonempty single-line title, at most 25 Unicode characters including spaces, without surrounding whitespace.",
      cancellation);
}
