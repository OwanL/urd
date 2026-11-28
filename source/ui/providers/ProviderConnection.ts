// A secret-free view of one saved provider connection, including its full binding.
export interface ProviderConnection {
  readonly id: string;
  readonly name: string;
  readonly route: ProviderRoute;
  readonly api: LlmApi;
  readonly endpoint: string;
  readonly authentication: AuthenticationMethod;
}

// Custom connections support API-key or unauthenticated HTTP endpoints.
export interface CustomProviderSettings {
  readonly name: string;
  readonly api: OpenAILlmApi;
  readonly endpoint: string;
  readonly authentication: "none" | "apiKey";
}

export type ProviderRoute = "openAI" | "chatGpt" | "gitHubCopilot" | "custom";
type OpenAILlmApi = "openAIResponses" | "openAIChatCompletions";
export type LlmApi = OpenAILlmApi | "anthropicMessages";
export type AuthenticationMethod = "none" | "apiKey" | "oAuth";
