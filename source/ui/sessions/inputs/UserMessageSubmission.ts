import type { LlmSelection } from "../../providers/LlmSelection.ts";

// Original submission; an explicit retry must preserve every member unchanged.
export interface UserMessageSubmission {
  readonly submissionId: string;
  readonly expectedHistoryRevision: number;
  readonly text: string;
  readonly imageIds?: readonly string[] | null;
  readonly llmSelection: LlmSelection | null;
}

// A saved admission decision, not evidence that the model consumed or completed the work.
export interface UserMessageReceipt {
  readonly sessionId: string;
  readonly submission: UserMessageSubmission;
  readonly acceptance: MessageAcceptance;
  readonly refusalReason: MessageRefusalReason | null;
  readonly executionId: string | null;
}

// Whether the original submission was admitted into an execution.
export type MessageAcceptance = "accepted" | "refused";

// The saved reason a submission was refused.
export type MessageRefusalReason = "staleRevision" | "capacity" | "stopping" | "selectionLocked";

// Reject malformed UTF-16 without trimming or replacing input.
export function submissionTextError(text: string): string | null {
  if (/[\uD800-\uDFFF]/u.test(text)) return "Message contains an incomplete Unicode character.";
  return null;
}
