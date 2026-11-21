import { WorkspaceCommands } from "../../workspace/WorkspaceCommands.ts";
import type {
  WorkspaceCommandAccess,
  WorkspaceCommandState,
} from "../../workspace/WorkspaceCommands.ts";
import { editDraft, newChat } from "../../chat/ChatState.ts";
import {
  decodeWorkspace,
  emptyWorkspace,
  encodeWorkspace,
} from "../../workspace/WorkspaceState.ts";
import type { WorkspaceState } from "../../workspace/WorkspaceState.ts";
import type { LlmSelection } from "../../providers/LlmSelection.ts";
import type { SessionClient } from "../../sessions/SessionClient.ts";
import type {
  ContinuationReceipt,
  ContinuationSubmission,
} from "../../sessions/inputs/Continuation.ts";
import type { SessionSnapshot } from "../../sessions/SessionSnapshot.ts";
import type { CompactionReceipt } from "../../sessions/inputs/Compaction.ts";
import type {
  UserMessageReceipt,
  UserMessageSubmission,
} from "../../sessions/inputs/UserMessageSubmission.ts";

export const sessionId = "10000000-0000-0000-0000-000000000001";
export const firstExecutionId = "20000000-0000-0000-0000-000000000002";
export const nextExecutionId = "20000000-0000-0000-0000-000000000003";
export const selection: LlmSelection = {
  connection: {
    id: "50000000-0000-0000-0000-000000000005",
    name: "Saved provider",
    route: "custom",
    api: "openAIResponses",
    endpoint: "http://127.0.0.1:1234/v1/",
    authentication: "none",
  },
  modelId: "model-id",
  family: "other",
  supportsImages: false,
  contextWindow: null,
  reasoningEffort: null,
};

export function snapshot(overrides: Partial<SessionSnapshot> = {}): SessionSnapshot {
  return {
    sessionId,
    historyRevision: 7,
    workingDirectory: null,
    canChangeWorkingDirectory: true,
    admissionFenced: false,
    canContinue: false,
    canCompact: true,
    unsettledExecutionId: null,
    stopRequested: false,
    lastSettlement: null,
    latestLlmSelection: selection,
    firstUserPreview: null,
    title: null,
    pruning: null,
    pendingInputs: [],
    ...overrides,
  };
}

export function fixture(): {
  read: () => WorkspaceState;
  reload: () => WorkspaceState;
  durable: () => WorkspaceState;
  access: WorkspaceCommandAccess;
  setWritable: (writable: boolean) => void;
} {
  let state = emptyWorkspace();
  state.tabs = [sessionId];
  state.activeId = sessionId;
  state.lastSelection = selection;
  state.chats[sessionId] = editDraft(newChat(selection), "submitted text");
  let writable = true;
  let persisted = encodeWorkspace(state);
  let durable = state;
  const persist = (next: WorkspaceState) => {
    state = next;
    if (!writable) return false;
    persisted = encodeWorkspace(next);
    durable = next;
    return true;
  };
  return {
    read: () => state,
    reload: () => {
      state = decodeWorkspace(persisted);
      return state;
    },
    durable: () => durable,
    setWritable: (next) => {
      writable = next;
    },
    access: {
      read: () => state,
      save: persist,
      updateChat: (id, update, lastSelection) => {
        const previous = state.chats[id];
        if (!previous) return false;
        return persist({
          ...state,
          lastSelection: lastSelection ?? state.lastSelection,
          chats: { ...state.chats, [id]: update(previous) },
        });
      },
      canPersist: () => true,
    },
  };
}

export function commands(
  access: WorkspaceCommandAccess,
  client: Pick<
    SessionClient,
    | "create"
    | "closeSession"
    | "submitMessage"
    | "getMessageReceipt"
    | "continueSession"
    | "getContinuationReceipt"
    | "startCompaction"
    | "cancelCompaction"
    | "setSubagentProviders"
    | "getCompactionOperation"
    | "removePendingInput"
    | "getSnapshot"
    | "stopExecution"
  >,
  onState: (state: WorkspaceCommandState) => void = () => {},
): WorkspaceCommands {
  return new WorkspaceCommands(
    client,
    access,
    onState,
    () => {},
    () => {},
  );
}

export function continuableSnapshot(overrides: Partial<SessionSnapshot> = {}): SessionSnapshot {
  return snapshot({
    canContinue: true,
    lastSettlement: { executionId: firstExecutionId, outcome: "completed" },
    ...overrides,
  });
}

export function receipt(submission: UserMessageSubmission): UserMessageReceipt {
  return {
    sessionId,
    submission,
    acceptance: "accepted",
    refusalReason: null,
    executionId: firstExecutionId,
  };
}

export function continuationReceipt(submission: ContinuationSubmission): ContinuationReceipt {
  return {
    sessionId,
    operationId: submission.operationId,
    executionId: submission.operationId,
  };
}

export function compactionReceipt(
  operationId: string,
  outcome: CompactionReceipt["outcome"] = "completed",
): CompactionReceipt {
  return { executionId: operationId, outcome };
}

export function emptyClient(
  submitMessage: SessionClient["submitMessage"] = async () => {
    throw new Error("Unexpected Send");
  },
  stopExecution: SessionClient["stopExecution"] = async (_id, executionId) => ({
    executionId,
    status: "requested",
    outcome: null,
  }),
): Pick<
  SessionClient,
  | "create"
  | "closeSession"
  | "submitMessage"
  | "getMessageReceipt"
  | "continueSession"
  | "getContinuationReceipt"
  | "startCompaction"
  | "cancelCompaction"
  | "setSubagentProviders"
  | "getCompactionOperation"
  | "removePendingInput"
  | "getSnapshot"
  | "stopExecution"
> {
  return {
    create: async () => sessionId,
    closeSession: async () => {
      throw new Error("Unexpected close");
    },
    submitMessage,
    getMessageReceipt: async () => null,
    continueSession: async (_id, submission) => continuationReceipt(submission),
    getContinuationReceipt: async () => null,
    startCompaction: async () => {
      throw new Error("Unexpected compaction");
    },
    cancelCompaction: async () => {
      throw new Error("Unexpected cancellation");
    },
    setSubagentProviders: async (_id, connectionIds) => ({ connectionIds }),
    getCompactionOperation: async () => null,
    removePendingInput: async () => true,
    getSnapshot: async () => snapshot(),
    stopExecution,
  };
}

export function deferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (cause: unknown) => void;
  const promise = new Promise<T>((complete, fail) => {
    resolve = complete;
    reject = fail;
  });
  return { promise, resolve, reject };
}

export async function withVisibleDocument<T>(action: () => Promise<T>): Promise<T> {
  const previous = Object.getOwnPropertyDescriptor(globalThis, "document");
  Object.defineProperty(globalThis, "document", {
    configurable: true,
    value: { hidden: false },
  });
  try {
    return await action();
  } finally {
    if (previous) Object.defineProperty(globalThis, "document", previous);
    else Reflect.deleteProperty(globalThis, "document");
  }
}
