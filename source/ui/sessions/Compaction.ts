import type { LlmSelection } from "../providers/LlmSelection.ts";
import type { ExecutionOutcome } from "./SessionExecutionResult.ts";

export interface CompactionSubmission {
  readonly operationId: string;
  readonly expectedHistoryRevision: number;
  readonly llmSelection: LlmSelection;
  readonly focus?: string | null;
}

export interface CompactionReceipt {
  readonly executionId: string;
  readonly outcome: ExecutionOutcome | null;
  readonly error?: string | null;
}

export type CompactionReason = "manual" | "threshold" | "overflow";

export interface CompactionSummary {
  readonly id: string;
  readonly sessionId: string;
  readonly historyRevision: number;
  readonly executionId: string;
  readonly summary: string;
  readonly reason: CompactionReason;
  readonly tokensBefore: number;
  readonly tokensAfter: number;
  readonly createdAt: string;
  readonly [cursorField: string]: unknown;
}
