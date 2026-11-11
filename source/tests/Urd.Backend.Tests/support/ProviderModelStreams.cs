using System.Text.Json;
using static Urd.Backend.Tests.Support.OpenAIStreams;

namespace Urd.Backend.Tests.Support;

internal static class ProviderModelStreams {
  internal const string Message = """{"type":"message","id":"msg_fixture","role":"assistant","status":"completed","content":[{"type":"output_text","text":"hello","annotations":[]}]}""";
  internal const string Reasoning = """{"type":"reasoning","id":"rs_fixture","encrypted_content":"private-reasoning","summary":[]}""";
  internal const string Call = """{"type":"function_call","id":"fc_fixture","status":"completed","call_id":"call_fixture","name":"lookup","arguments":"{\"key\":\"abc\"}"}""";

  internal static string Responses(string[]? items = null, string? terminal = "response.completed", string text = "hello",
      bool itemDone = true, string? terminalItems = null) {
    items ??= [Message];
    JsonElement[] output = items.Select(item => JsonSerializer.Deserialize<JsonElement>(item)).ToArray();
    object Envelope(string status, object nativeOutput) => new {
      id = "resp_fixture", @object = "response", created_at = 1700000000, model = "fixture-model", status,
      store = false, output = nativeOutput,
      error = status == "failed" ? new { code = "server_error", message = "synthetic-private" } : null,
      incomplete_details = status == "incomplete" ? new { reason = "max_output_tokens" } : null
    };
    int sequence = 0;
    string stream = Event("response.created", new { type = "response.created", sequence_number = sequence++, response = Envelope("in_progress", Array.Empty<object>()) });
    if (text.Length > 0) stream += Event("response.output_text.delta", new {
      type = "response.output_text.delta", sequence_number = sequence++, item_id = "msg_fixture", output_index = 0, content_index = 0, delta = text
    });
    if (itemDone) {
      for (int index = 0; index < output.Length; index++) stream += Event("response.output_item.done", new {
        type = "response.output_item.done", sequence_number = sequence++, output_index = index, item = output[index]
      });
    }
    if (terminal is not null) stream += Event(terminal, new {
      type = terminal, sequence_number = sequence, response = Envelope(terminal.Split('.')[1],
          terminalItems is null ? output : JsonSerializer.Deserialize<JsonElement[]>(terminalItems)!)
    });
    return stream;
  }

  internal static string ChatCalls(string finish = "tool_calls", bool reasoning = false,
      string arguments = "{\"key\":\"abc\"}", string name = "lookup") {
    string Chunk(object delta, string? reason = null) => "data: " + JsonSerializer.Serialize(new {
      id = "chatcmpl_fixture", @object = "chat.completion.chunk", created = 1700000000, model = "fixture-model",
      choices = new[] { new { index = 0, delta, finish_reason = reason } }
    }) + "\n\n";
    return Chunk(new { role = "assistant", content = "hello", reasoning_content = reasoning ? "thought" : null }) +
        Chunk(new { tool_calls = new[] { new { index = 0, id = "call_fixture", type = "function", function = new { name, arguments = arguments[..(arguments.Length / 2)] } } } }) +
        Chunk(new { tool_calls = new[] { new { index = 0, function = new { arguments = arguments[(arguments.Length / 2)..] } } } }) +
        Chunk(new { }, finish) + "data: [DONE]\n\n";
  }
}
