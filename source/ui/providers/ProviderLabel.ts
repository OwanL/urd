import type { ProviderConnection } from "./ProviderConnection.ts";

export function providerLabel(connection: ProviderConnection): string {
  switch (connection.route) {
    case "chatGpt":
    case "openAI":
      return "OpenAI";
    case "gitHubCopilot":
      return "GitHub Copilot";
    case "custom":
      return connection.name;
  }
}
