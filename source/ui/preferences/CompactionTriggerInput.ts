export const maximumCompactionTriggerTokenCount = 2_147_483_647;
// Matches the server's ContextTokenBudget default. Reset still sends null, not this number.
export const defaultCompactionTriggerTokenCount = 150_000;
export const compactionSlider = { min: 5_000, max: 300_000, step: 5_000 } as const;

// Exact overrides can be off-scale or between steps. Move only the thumb, not the draft.
export function compactionSliderPosition(tokenCount: number): number {
  const { min, max, step } = compactionSlider;
  return Math.min(max, Math.max(min, Math.round(tokenCount / step) * step));
}

export function compactionTriggerInput(value: number | null): string {
  return value === null ? "" : String(value);
}

export function reconcileCompactionTriggerDraft(
  draft: string,
  edited: boolean,
  savedValue: number | null,
): { readonly value: string; readonly edited: boolean } {
  const savedDraft = compactionTriggerInput(savedValue);
  if (edited && draft !== savedDraft) return { value: draft, edited: true };
  return { value: savedDraft, edited: false };
}

export type CompactionTriggerInputResult =
  | { readonly valid: true; readonly value: number | null }
  | { readonly valid: false; readonly error: string };

export function parseCompactionTriggerInput(value: string): CompactionTriggerInputResult {
  if (value === "") return { valid: true, value: null };
  if (!/^\d+$/.test(value)) return invalidInput();

  const tokenCount = Number(value);
  if (
    !Number.isSafeInteger(tokenCount) ||
    tokenCount < 1 ||
    tokenCount > maximumCompactionTriggerTokenCount
  )
    return invalidInput();

  return { valid: true, value: tokenCount };
}

function invalidInput(): CompactionTriggerInputResult {
  return {
    valid: false,
    error: "Enter a whole number from 1 to 2,147,483,647, or leave blank for the default.",
  };
}
