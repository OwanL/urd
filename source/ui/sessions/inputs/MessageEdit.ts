import type { LlmSelection } from "../../providers/LlmSelection.ts";
import type { MessageRef, TranscriptEntryRef, UserImage } from "../transcript/Messages.ts";

export interface MessageEditSubmission {
  readonly operationId: string;
  readonly message: MessageRef;
  readonly text: string;
  readonly imageIds?: readonly string[] | null;
  readonly llmSelection: LlmSelection;
}

export interface MessageEditReceipt {
  readonly sessionId: string;
  readonly operationId: string;
  readonly historyRevision: number;
  readonly executionId: string;
}

export interface MessageEditDraft {
  readonly target: TranscriptEntryRef;
  readonly text: string;
  readonly images?: readonly UserImage[];
  readonly status: "loading" | "editing" | "saving" | "uncertain";
  readonly submission?: MessageEditSubmission | undefined;
  readonly error: string | null;
}
