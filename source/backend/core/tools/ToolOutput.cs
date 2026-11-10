using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Urd.Core.Tools;

// A tool's text and optional images, with structured detail retained for consumers.
internal sealed record ToolOutput(
  string Text, IReadOnlyList<DataContent> Images,
  JsonElement? Details = null, bool IsError = false);
