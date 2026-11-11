using System.Text;

namespace Urd.Core.ToolCallDisplay;

internal static class ToolCallLabelText {
  internal static string OneLine(string value, int maximumLength) {
    var singleLine = new StringBuilder(value.Length);
    bool previousWasSpace = true;
    foreach (char character in value) {
      bool isSpace = char.IsWhiteSpace(character) || char.IsControl(character);
      if (isSpace) {
        if (!previousWasSpace)
          singleLine.Append(' ');
        previousWasSpace = true;
        continue;
      }
      singleLine.Append(character);
      previousWasSpace = false;
    }
    if (singleLine.Length > 0 && singleLine[^1] == ' ')
      singleLine.Length--;

    if (singleLine.Length <= maximumLength)
      return singleLine.ToString();

    int end = maximumLength - 1;
    if (end > 0 && char.IsHighSurrogate(singleLine[end - 1]))
      end--;
    while (end > 0 && singleLine[end - 1] == ' ')
      end--;
    return string.Concat(singleLine.ToString(0, end), "…");
  }
}
