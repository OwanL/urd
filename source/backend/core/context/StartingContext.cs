using Microsoft.Extensions.AI;
using Urd.Core.Providers;

namespace Urd.Core.Context;

// The supported session roles for starting-context composition.
internal enum SessionKind { Primary, Subagent }

// Inputs used to compose instructions and model tool declarations for one session.
internal sealed record StartingContextInput(
  SessionKind SessionKind,
  ModelFamily ModelFamily,
  string? WorkingDirectory,
  string InitialRequest,
  string? AssignmentContext);

// Read-only instructions and tool declarations supplied when a model session starts.
internal sealed record StartingContext(
  IReadOnlyList<ChatMessage> Instructions,
  IReadOnlyList<AIFunctionDeclaration> Tools);
