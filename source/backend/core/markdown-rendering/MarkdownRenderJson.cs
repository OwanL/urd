using System.Text.Json;

namespace Urd.Core.MarkdownRendering;

public static class MarkdownRenderJson {
  public static JsonSerializerOptions Options { get; } = CreateOptions();

  static JsonSerializerOptions CreateOptions() {
    var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
    options.MakeReadOnly(populateMissingResolver: true);
    return options;
  }
}
