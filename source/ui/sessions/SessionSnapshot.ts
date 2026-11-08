import type { LlmSelection } from "../providers/LlmSelection.ts";
import type { MessagePreview } from "./Messages.ts";
import type { SessionExecutionResult } from "./SessionExecutionResult.ts";

// Bounded committed session facts combined with current admission and Stop state.
export interface SessionSnapshot {
  readonly sessionId: string;
  readonly historyRevision: number;
  readonly admissionFenced: boolean;
  readonly unsettledExecutionId: string | null;
  readonly stopRequested: boolean;
  readonly lastSettlement: SessionExecutionResult | null;
  readonly latestLlmSelection: LlmSelection | null;
  readonly messages: readonly MessagePreview[];
  readonly hasOlderMessages: boolean;
}
