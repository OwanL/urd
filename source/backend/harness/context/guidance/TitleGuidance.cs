namespace Urd.Core.Context;

// Guidance for naming a session from its first user message.
internal static class TitleGuidance {
  internal const int MaximumCharacters = 25;

  internal static readonly string System = "Name this session from its first user message. "
      + "Answer directly; don't use tools. "
      + "Read only if you can't identify the topic from that message. "
      + $"Return only a short plain-text title, at most {MaximumCharacters} characters including spaces. "
      + "No quotes, markup or explanation.";

  internal static readonly string Correction = "Invalid title. Return only a nonempty single-line title, "
      + $"at most {MaximumCharacters} Unicode characters including spaces, without surrounding whitespace.";
}
