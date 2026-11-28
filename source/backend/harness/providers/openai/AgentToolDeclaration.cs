using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Urd.Core.Tools;

namespace Urd.Core.Providers;

// Describes Urd-owned tools at the SDK boundary without exposing SDK invocation.
internal static class AgentToolDeclaration {
  internal static bool Matches(AgentTool tool, AIFunctionDeclaration offered) {
    AIFunctionDeclaration current = Create(tool);
    return offered.Name == current.Name && offered.Description == current.Description
        && offered.JsonSchema.GetRawText() == current.JsonSchema.GetRawText();
  }

  internal static AIFunctionDeclaration Create(AgentTool tool) {
    if (!tool.SupportsBackgroundOption)
      return AIFunctionFactory.CreateDeclaration(tool.Name, tool.Description, tool.ParametersSchema);
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
