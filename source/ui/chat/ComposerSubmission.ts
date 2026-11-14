export interface ComposerSubmissionState {
  readonly action: "send" | "continue" | null;
  readonly label: "Send" | "Continue";
}

export function composerSubmissionState(
  text: string,
  canSend: boolean,
  canContinue: boolean,
  continuationAvailable: boolean,
): ComposerSubmissionState {
  const hasMessage = Boolean(text.trim());
  return {
    action: hasMessage ? (canSend ? "send" : null) : canContinue ? "continue" : null,
    label: !hasMessage && continuationAvailable ? "Continue" : "Send",
  };
}
