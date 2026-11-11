// Explicit model metadata; IDs and connection routes do not imply capabilities.
export type ModelFamily = "openAI" | "anthropic" | "google" | "other";

export interface ModelSettings {
  readonly connectionId: string;
  readonly modelId: string;
  readonly family: ModelFamily;
  readonly supportsImages: boolean;
  readonly contextWindow: number | null;
}
