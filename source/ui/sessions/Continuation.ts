import type { LlmSelection } from "../providers/LlmSelection.ts";

export interface ContinuationSubmission {
  readonly operationId: string;
  readonly expectedHistoryRevision: number;
  readonly expectedLastExecutionId: string;
  readonly llmSelection: LlmSelection;
}

export interface ContinuationReceipt {
  readonly sessionId: string;
  readonly operationId: string;
  readonly executionId: string;
}
