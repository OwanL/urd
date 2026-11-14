using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Urd.Core.Tools;

namespace Urd.Core.Providers;

// Describes Urd-owned tools at the SDK boundary without exposing SDK invocation.
internal static class AgentToolDeclaration {
  internal static AIFunctionDeclaration Create(AgentTool tool) {
    JsonObject schema = JsonNode.Parse(tool.ParametersSchema.GetRawText())!.AsObject();
    JsonObject properties = schema["properties"]?.AsObject() ?? new JsonObject();
    if (schema["properties"] is null)
      schema["properties"] = properties;
    properties.Add("background", new JsonObject {
      ["type"] = "boolean",
      ["description"] = "Run in the background and deliver the result separately; omitted or false waits for the result."
    });
    return AIFunctionFactory.CreateDeclaration(tool.Name, tool.Description,
        JsonSerializer.SerializeToElement(schema));
  }
}
