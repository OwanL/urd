import type { LlmSelection } from "../providers/LlmSelection.ts";
import type { ExecutionOutcome } from "./SessionExecutionResult.ts";

export interface ManualCompactionRequest {
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
  readonly summary: string;
  readonly reason: CompactionReason;
  readonly estimatedContextTokensBefore: number;
  readonly estimatedContextTokensAfter: number;
  readonly createdAt: string;
}
