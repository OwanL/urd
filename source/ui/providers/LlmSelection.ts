import type { ProviderConnection } from "./ProviderConnection.ts";
import type { ModelFamily } from "./ModelSettings.ts";

// The initiating choice bound to an execution, not just a mutable connection ID.
export interface LlmSelection {
  readonly connection: ProviderConnection;
  readonly modelId: string;
  readonly reasoningEffort: string | null;
  readonly family: ModelFamily;
  readonly supportsImages: boolean;
  readonly contextWindow: number | null;
}

export function sameContextModel(left: LlmSelection | null, right: LlmSelection | null): boolean {
  if (!left || !right || left.modelId !== right.modelId) return false;
  const a = left.connection;
  const b = right.connection;
  return (
    a.id === b.id &&
    a.route === b.route &&
    a.api === b.api &&
    a.endpoint === b.endpoint &&
    a.authentication === b.authentication
  );
}
