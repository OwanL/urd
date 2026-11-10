import type { ProviderConnection } from "./ProviderConnection.ts";
import type { ModelFamily } from "./ModelSettings.ts";

// The initiating choice bound to an execution, not just a mutable connection ID.
export interface LlmSelection {
  readonly connection: ProviderConnection;
  readonly modelId: string;
  readonly reasoningEffort: string | null;
  readonly family: ModelFamily;
  readonly supportsImages: boolean;
}
