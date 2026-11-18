namespace Urd.Core.Sessions;

// Placeholder titles are display-only; only final titles are communication addresses.
internal sealed record AgentTitle(string Text, bool IsPlaceholder);
internal sealed record LiveAgent(AgentTitle Title, string State, AgentTitle? ParentTitle);
