// Saved execution outcome, published only after owned work and cleanup finish.
export interface SessionExecutionResult {
  readonly executionId: string;
  readonly outcome: ExecutionOutcome;
  readonly error?: string | null;
}

// Shared wire outcome for an execution, LLM request or tool invocation.
export type ExecutionOutcome = "completed" | "cancelled" | "failed";
