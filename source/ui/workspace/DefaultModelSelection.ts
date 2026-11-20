import type { LlmSelection } from "../providers/LlmSelection.ts";
import type { ModelOption } from "../providers/models/ModelOptions.ts";

export function defaultModelSelection(
  options: readonly ModelOption[],
  remembered: LlmSelection | null,
  history: LlmSelection | null,
  discovering: boolean,
): LlmSelection | null {
  for (const choice of [remembered, history]) {
    if (!choice) continue;
    const match = options.find(
      ({ selection }) =>
        selection.connection.id === choice.connection.id && selection.modelId === choice.modelId,
    );
    if (match) return { ...match.selection, reasoningEffort: choice.reasoningEffort };
    if (discovering) return null;
  }
  return discovering ? null : (options[0]?.selection ?? null);
}
