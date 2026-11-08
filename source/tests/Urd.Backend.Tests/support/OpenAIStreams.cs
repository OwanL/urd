using System.Text.Json;

namespace Urd.Backend.Tests.Support;

// Shared native wire fixtures for SDK, manager and real-server provider tests.
internal static class OpenAIStreams {
  internal static string Event(string type, object data) => $"event: {type}\ndata: {JsonSerializer.Serialize(data)}\n\n";

  internal static string ResponseStream(string? terminal = "response.completed", string text = "hello", object? usage = null,
      string? status = null) {
    object Envelope(string state) => new {
      id = "resp_fixture", @object = "response", created_at = 1700000000, model = "fixture-model", status = state,
      store = false, output = Array.Empty<object>(), usage,
      error = state == "failed" ? new { code = "server_error", message = "synthetic-private" } : null,
      incomplete_details = state == "incomplete" ? new { reason = "max_output_tokens" } : null
    };
    string stream = Event("response.created", new { type = "response.created", sequence_number = 0, response = Envelope("in_progress") }) +
        Event("response.output_text.delta", new {
          type = "response.output_text.delta", sequence_number = 1, item_id = "msg_fixture", output_index = 0, content_index = 0, delta = text
        });
    if (terminal is null) return stream;
    if (terminal == "error") return stream + Event("error", new { type = "error", sequence_number = 2, code = "server_error", message = "synthetic-private" });
    return stream + Event(terminal, new { type = terminal, sequence_number = 2, response = Envelope(status ?? terminal.Split('.')[1]) });
  }

  internal static string ChatStream(string? finish = "stop", object? usage = null, bool done = true) {
    string Chunk(object[] choices, object? tokenUsage = null) => "data: " + JsonSerializer.Serialize(new {
      id = "chatcmpl_fixture", @object = "chat.completion.chunk", created = 1700000000, model = "fixture-model", choices, usage = tokenUsage
    }) + "\n\n";
    string stream = Chunk([new { index = 0, delta = new { role = "assistant", content = "hello" }, finish_reason = (string?)null }]);
    if (finish is not null) stream += Chunk([new { index = 0, delta = new { }, finish_reason = finish }]);
    if (usage is not null) stream += Chunk([], usage);
    return done ? stream + "data: [DONE]\n\n" : stream;
  }
}
