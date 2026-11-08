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
  readonly api: LlmApi;
  readonly endpoint: string;
  readonly authentication: "none" | "apiKey";
}

export type ProviderRoute = "openAI" | "chatGpt" | "custom";
export type LlmApi = "openAIResponses" | "openAIChatCompletions";
export type AuthenticationMethod = "none" | "apiKey" | "oAuth";
