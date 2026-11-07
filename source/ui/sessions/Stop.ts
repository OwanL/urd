import type { ExecutionOutcome } from './SessionExecutionResult.ts';

// Current Stop acknowledgement; requested does not mean cleanup has finished.
export interface StopReceipt {
  readonly executionId: string;
  readonly status: StopStatus;
  readonly outcome: ExecutionOutcome | null;
}

// Unavailable is retained in the HTTP 409 error body; missing executions return 404.
export type StopStatus = 'requested' | 'settled' | 'notFound' | 'unavailable';
