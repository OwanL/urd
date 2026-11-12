import type { LlmSelection } from "../providers/LlmSelection.ts";
import type { SessionExecutionResult } from "./SessionExecutionResult.ts";

// Bounded committed session facts combined with current admission and Stop state.
export interface PendingInputPreview {
  readonly submissionId: string;
  readonly preview: string;
}

export interface SessionWorkingTime {
  readonly completedMilliseconds: number;
  readonly startedAt: string | null;
  readonly hasUnknownDuration: boolean;
}

export interface SessionContextUsage {
  readonly usedTokens: number;
  readonly isEstimate: boolean;
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
  readonly workingTime?: SessionWorkingTime | null;
  readonly contextUsage?: SessionContextUsage | null;
}
