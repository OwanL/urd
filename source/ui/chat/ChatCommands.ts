import type {
  SubagentProviderConnectionIds,
  SubagentProviders,
} from "../providers/subagents/SubagentProviderSelection.ts";
import type { LlmSelection } from "../providers/LlmSelection.ts";
import type { ContinuationReceipt } from "../sessions/inputs/Continuation.ts";
import type { UserMessageReceipt } from "../sessions/inputs/UserMessageSubmission.ts";
import type { SessionSnapshot } from "../sessions/SessionSnapshot.ts";

// Status of a stop request shown by the chat.
export interface ChatStopState {
  readonly executionId: string;
  readonly pending: boolean;
  readonly error: string | null;
}

// Status of a pending-input removal shown by the chat.
export interface PendingInputRemovalState {
  readonly sessionId: string;
  readonly executionId: string;
  readonly submissionId: string;
  readonly status: "removing" | "checking" | "uncertain";
}

// Status of a compaction shown by the chat.
export type ChatCompactionState =
  | { readonly phase: "starting" | "observing"; readonly operationId: string }
  | { readonly phase: "uncertain"; readonly operationId: string; readonly message: string }
  | { readonly phase: "settled"; readonly message: string };

// Commands and status consumed by the chat view.
export interface ChatCommands {
  readonly status: {
    readonly savingSubagentProviders: ReadonlySet<string>;
    readonly subagentProviderError: { readonly sessionId: string; readonly message: string } | null;
    readonly compactions: Readonly<Record<string, ChatCompactionState>>;
    readonly pendingInputRemovals: Readonly<Record<string, PendingInputRemovalState>>;
  };
  observeSnapshot(snapshot: SessionSnapshot, knownWhenRead: string | null): void;
  hasPendingInputRemoval(sessionId: string): boolean;
  pendingInputRemoval(
    sessionId: string,
    executionId: string,
    submissionId: string,
  ): PendingInputRemovalState | undefined;
  removePendingInput(
    id: string,
    executionId: string,
    submissionId: string,
    canRemove: boolean,
  ): Promise<void>;
  checkPendingInputRemoval(id: string, executionId: string, submissionId: string): Promise<void>;
  saveSubagentProviders(
    id: string,
    connectionIds: SubagentProviderConnectionIds,
  ): Promise<SubagentProviders | null>;
  compact(
    id: string,
    snapshot: SessionSnapshot,
    canCompact: boolean,
    defaultSelection: LlmSelection | null,
  ): Promise<void>;
  send(
    id: string,
    snapshot: SessionSnapshot,
    active: boolean,
    canSend: boolean,
    defaultSelection: LlmSelection | null,
  ): Promise<void>;
  continueSession(
    id: string,
    snapshot: SessionSnapshot,
    canContinue: boolean,
    defaultSelection: LlmSelection | null,
  ): Promise<void>;
  stop(id: string, snapshot: SessionSnapshot, stopId: string | undefined): Promise<void>;
  checkReceipt(id: string): Promise<UserMessageReceipt | null>;
  retryOriginalSubmission(id: string): Promise<UserMessageReceipt | null>;
  checkContinuationReceipt(id: string): Promise<ContinuationReceipt | null>;
  retryOriginalContinuation(id: string): Promise<ContinuationReceipt | null>;
}
