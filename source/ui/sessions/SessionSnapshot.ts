import type { LlmSelection } from "../providers/LlmSelection.ts";
import type { SessionExecutionResult } from "./SessionExecutionResult.ts";
import type { PendingResponse } from "./transcript/Prepass.ts";

// Bounded committed session facts combined with current admission and Stop state.
export interface PendingInputPreview {
  readonly submissionId: string;
  readonly preview: string;
  readonly isClaimed: boolean;
  // Missing capability fields in older fixtures are treated as non-removable.
  readonly canRemove?: boolean;
}

export interface SessionWorkingTime {
  readonly completedMilliseconds: number;
  readonly startedAt: string | null;
  readonly hasUnknownDuration: boolean;
}

export interface SessionContextUsage {
  readonly usedTokens: number;
  readonly source: "measured" | "estimated" | "lastMeasured";
}

export interface SessionPruningStatus {
  readonly category: string;
  readonly state: string;
  readonly total: number;
  readonly retained: number;
  readonly error?: string | null;
}

export interface SubagentViewSnapshot {
  readonly historyRevision: number;
  readonly live: boolean;
  readonly title: string;
  readonly parentId: string;
  readonly modelId: string | null;
  readonly reasoningEffort: string | null;
  readonly contextWindow: number | null;
  readonly contextUsage: SessionContextUsage | null;
  readonly workingTime: SessionWorkingTime | null;
  readonly admissionFenced: boolean;
}

export interface SessionSnapshot {
  readonly sessionId: string;
  readonly historyRevision: number;
  readonly workingDirectory: string | null;
  readonly canChangeWorkingDirectory: boolean;
  readonly admissionFenced: boolean;
  readonly requiresRecovery?: boolean;
  readonly canCloseAfterFailure?: boolean;
  readonly canContinue: boolean;
  readonly canCompact?: boolean;
  readonly queuedCompactionId?: string | null;
  readonly manualCompactionId?: string | null;
  readonly compactionUnavailableReason?: string | null;
  readonly unsettledExecutionId: string | null;
  readonly queuedInputExecutionId?: string | null;
  readonly isCompacting?: boolean;
  readonly rateLimitRetryAt?: string | null;
  readonly stopRequested: boolean;
  readonly lastSettlement: SessionExecutionResult | null;
  readonly latestLlmSelection: LlmSelection | null;
  readonly firstUserPreview: string | null;
  readonly title?: string | null;
  readonly pruning?: readonly SessionPruningStatus[] | null;
  readonly pendingResponse?: PendingResponse | null;
  readonly pendingInputs: readonly PendingInputPreview[];
  readonly workingTime?: SessionWorkingTime | null;
  readonly contextUsage?: SessionContextUsage | null;
}
