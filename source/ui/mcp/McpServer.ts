// Wire records for MCP server settings and connection operations.
export type McpAuthentication = "automatic" | "bearerToken" | "customHeader";

export interface McpServer {
  id: string;
  name: string;
  url: string;
  authentication: McpAuthentication;
  tools: string[];
}

export interface McpOperation {
  state: "connecting" | "signIn" | "connected" | "failed";
  authorizationUrl: string | null;
  message: string | null;
}
