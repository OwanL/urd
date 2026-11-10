using Microsoft.Extensions.AI;
using Urd.Core.Tools;

namespace Urd.Core.Providers;

// Describes Urd-owned tools at the SDK boundary without exposing SDK invocation.
internal static class AgentToolDeclaration {
  internal static AIFunctionDeclaration Create(AgentTool tool) =>
      AIFunctionFactory.CreateDeclaration(tool.Name, tool.Description, tool.ParametersSchema);
}
