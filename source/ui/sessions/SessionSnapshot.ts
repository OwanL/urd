import type { LlmSelection } from "../providers/LlmSelection.ts";
import type { SessionExecutionResult } from "./SessionExecutionResult.ts";

// Bounded committed session facts combined with current admission and Stop state.
export interface PendingInputPreview {
  readonly submissionId: string;
  readonly preview: string;
}

export interface SessionSnapshot {
  readonly sessionId: string;
  readonly historyRevision: number;
  readonly admissionFenced: boolean;
  readonly unsettledExecutionId: string | null;
  readonly isCompacting?: boolean;
  readonly stopRequested: boolean;
  readonly lastSettlement: SessionExecutionResult | null;
  readonly latestLlmSelection: LlmSelection | null;
  readonly firstUserPreview: string | null;
  readonly pendingInputs: readonly PendingInputPreview[];
}
