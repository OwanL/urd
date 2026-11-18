import assert from "node:assert/strict";
import test from "node:test";
import { WorkspaceCommands } from "../app/WorkspaceCommands.ts";
import { RequestError } from "../http/RequestError.ts";
import type { WorkspaceCommandAccess, WorkspaceCommandState } from "../app/WorkspaceCommands.ts";
import {
  decodeWorkspace,
  editDraft,
  emptyWorkspace,
  encodeWorkspace,
  newChat,
} from "../app/WorkspaceState.ts";
import type { WorkspaceState } from "../app/WorkspaceState.ts";
import type { LlmSelection } from "../providers/LlmSelection.ts";
import type { SessionClient } from "../sessions/SessionClient.ts";
import type { ContinuationReceipt, ContinuationSubmission } from "../sessions/Continuation.ts";
import type { SessionSnapshot } from "../sessions/SessionSnapshot.ts";
import type { CompactionReceipt } from "../sessions/Compaction.ts";
import type { StopReceipt } from "../sessions/Stop.ts";
import type {
  UserMessageReceipt,
  UserMessageSubmission,
} from "../sessions/UserMessageSubmission.ts";

const sessionId = "10000000-0000-0000-0000-000000000001";
const firstExecutionId = "20000000-0000-0000-0000-000000000002";
const nextExecutionId = "20000000-0000-0000-0000-000000000003";
const selection: LlmSelection = {
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

function snapshot(overrides: Partial<SessionSnapshot> = {}): SessionSnapshot {
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

function fixture(): {
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
      updateChat: (id, update) => {
        const previous = state.chats[id];
        if (!previous) return false;
        return persist({ ...state, chats: { ...state.chats, [id]: update(previous) } });
      },
      canPersist: () => true,
    },
  };
}

function commands(
  access: WorkspaceCommandAccess,
  client: Pick<
    SessionClient,
    | "create"
    | "submitMessage"
    | "getMessageReceipt"
    | "continueSession"
    | "getContinuationReceipt"
    | "startCompaction"
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

function continuableSnapshot(overrides: Partial<SessionSnapshot> = {}): SessionSnapshot {
  return snapshot({
    canContinue: true,
    lastSettlement: { executionId: firstExecutionId, outcome: "completed" },
    ...overrides,
  });
}

function receipt(submission: UserMessageSubmission): UserMessageReceipt {
  return {
    sessionId,
    submission,
    acceptance: "accepted",
    refusalReason: null,
    executionId: firstExecutionId,
  };
}

function continuationReceipt(submission: ContinuationSubmission): ContinuationReceipt {
  return {
    sessionId,
    operationId: submission.operationId,
    executionId: submission.operationId,
  };
}

function compactionReceipt(
  operationId: string,
  outcome: CompactionReceipt["outcome"] = "completed",
): CompactionReceipt {
  return { executionId: operationId, outcome };
}

function emptyClient(
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
  | "submitMessage"
  | "getMessageReceipt"
  | "continueSession"
  | "getContinuationReceipt"
  | "startCompaction"
  | "getCompactionOperation"
  | "removePendingInput"
  | "getSnapshot"
  | "stopExecution"
> {
  return {
    create: async () => sessionId,
    submitMessage,
    getMessageReceipt: async () => null,
    continueSession: async (_id, submission) => continuationReceipt(submission),
    getContinuationReceipt: async () => null,
    startCompaction: async () => {
      throw new Error("Unexpected compaction");
    },
    getCompactionOperation: async () => null,
    removePendingInput: async () => true,
    getSnapshot: async () => snapshot(),
    stopExecution,
  };
}

function deferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (cause: unknown) => void;
  const promise = new Promise<T>((complete, fail) => {
    resolve = complete;
    reject = fail;
  });
  return { promise, resolve, reject };
}

async function withVisibleDocument<T>(action: () => Promise<T>): Promise<T> {
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

void test("a failed synchronous Send journal stays unsaved and dispatches nothing", async () => {
  const workspace = fixture();
  workspace.setWritable(false);
  let sends = 0;
  const coordinator = commands(
    workspace.access,
    emptyClient(async () => {
      sends++;
      throw new Error("must not dispatch");
    }),
  );

  await coordinator.send(sessionId, snapshot(), false, true);

  const pending = workspace.read().chats[sessionId]?.pending;
  assert.equal(sends, 0);
  assert.equal(pending?.status, "uncertain");
  assert.equal(pending?.original.text, "submitted text");
  assert.equal(workspace.reload().chats[sessionId]?.pending, null);
});

void test("a failed synchronous Continue journal dispatches nothing", async () => {
  const workspace = fixture();
  workspace.setWritable(false);
  let posts = 0;
  const coordinator = commands(workspace.access, {
    ...emptyClient(),
    continueSession: async () => {
      posts++;
      throw new Error("must not dispatch");
    },
  });

  await coordinator.continueSession(sessionId, continuableSnapshot(), true);

  assert.equal(posts, 0);
  assert.equal(workspace.read().chats[sessionId]?.pendingContinuation?.status, "uncertain");
  assert.equal(workspace.reload().chats[sessionId]?.pendingContinuation, null);
});

void test("retry resends a saved uncertain original while retaining a newer draft", async () => {
  const workspace = fixture();
  let loseFirstAcknowledgement = true;
  let firstJournaledBeforeDispatch = false;
  const initial = commands(
    workspace.access,
    emptyClient(async (_id, submission) => {
      firstJournaledBeforeDispatch =
        workspace.durable().chats[sessionId]?.pending?.status === "sending";
      if (loseFirstAcknowledgement) {
        loseFirstAcknowledgement = false;
        throw new Error("acknowledgement lost");
      }
      return receipt(submission);
    }),
  );

  await initial.send(sessionId, snapshot(), false, true);
  assert.equal(firstJournaledBeforeDispatch, true);
  assert.equal(workspace.read().chats[sessionId]?.title, "New chat");
  assert.equal(workspace.durable().chats[sessionId]?.pending?.status, "uncertain");
  assert.equal(workspace.reload().chats[sessionId]?.pending?.status, "uncertain");
  workspace.access.updateChat(sessionId, (previous) => editDraft(previous, "newer draft"));
  const original = workspace.read().chats[sessionId]!.pending!.original;
  let dispatched: UserMessageSubmission | null = null;
  let journaledBeforeDispatch = false;
  const retry = commands(
    workspace.access,
    emptyClient(async (_id, submission) => {
      dispatched = submission;
      journaledBeforeDispatch = workspace.durable().chats[sessionId]?.pending?.status === "sending";
      return receipt(submission);
    }),
  );

  await retry.retryOriginalSubmission(sessionId);

  assert.deepEqual(dispatched, original);
  assert.equal(journaledBeforeDispatch, true);
  assert.equal(workspace.read().chats[sessionId]?.pending, null);
  assert.equal(workspace.read().chats[sessionId]?.draft, "newer draft");
});

void test("Continue journals before dispatch, checks after reload, and retries the exact frozen operation", async () => {
  const workspace = fixture();
  const submissions: ContinuationSubmission[] = [];
  let journaledBeforeDispatch = false;
  const initial = commands(workspace.access, {
    ...emptyClient(),
    continueSession: async (_id, submission) => {
      submissions.push(submission);
      journaledBeforeDispatch =
        workspace.durable().chats[sessionId]?.pendingContinuation?.status === "sending";
      throw new Error("acknowledgement lost");
    },
  });

  await initial.continueSession(sessionId, continuableSnapshot({ historyRevision: 14 }), true);
  assert.equal(journaledBeforeDispatch, true);
  const firstSubmission = submissions[0]!;
  assert.deepEqual(firstSubmission, {
    operationId: firstSubmission.operationId,
    expectedHistoryRevision: 14,
    expectedLastExecutionId: firstExecutionId,
    llmSelection: selection,
  });
  assert.equal(workspace.read().chats[sessionId]?.pendingContinuation?.status, "uncertain");
  assert.equal(workspace.read().chats[sessionId]?.draft, "submitted text");
  assert.equal(workspace.read().chats[sessionId]?.title, "New chat");

  workspace.reload();
  const restored = workspace.read().chats[sessionId]?.pendingContinuation;
  assert.equal(restored?.status, "uncertain");
  assert.deepEqual(restored?.original, firstSubmission);
  let posts = 0;
  const recovery = commands(workspace.access, {
    ...emptyClient(),
    continueSession: async () => {
      posts++;
      throw new Error("receipt check must not POST");
    },
    getContinuationReceipt: async () => null,
  });
  await withVisibleDocument(() => recovery.checkContinuationReceipt(sessionId));
  assert.equal(posts, 0);
  assert.equal(workspace.read().chats[sessionId]?.pendingContinuation?.status, "uncertain");

  workspace.access.updateChat(sessionId, (previous) => editDraft(previous, "draft typed later"));
  const retries: ContinuationSubmission[] = [];
  let retryJournaled = false;
  const retry = commands(workspace.access, {
    ...emptyClient(),
    continueSession: async (_id, submission) => {
      retries.push(submission);
      retryJournaled =
        workspace.durable().chats[sessionId]?.pendingContinuation?.status === "sending";
      return continuationReceipt(submission);
    },
  });
  await retry.retryOriginalContinuation(sessionId);

  assert.deepEqual(retries, [firstSubmission]);
  assert.equal(retryJournaled, true);
  assert.equal(workspace.read().chats[sessionId]?.pendingContinuation, null);
  assert.equal(workspace.read().chats[sessionId]?.knownExecutionId, firstSubmission.operationId);
  assert.equal(workspace.read().chats[sessionId]?.draft, "draft typed later");
  assert.equal(workspace.read().chats[sessionId]?.title, "New chat");
});

void test("a definite Continue conflict checks for an earlier accepted receipt before clearing its journal", async () => {
  const workspace = fixture();
  const first = commands(workspace.access, {
    ...emptyClient(),
    continueSession: async () => {
      throw new Error("acknowledgement lost");
    },
  });
  await first.continueSession(sessionId, continuableSnapshot(), true);
  const original = workspace.read().chats[sessionId]!.pendingContinuation!.original;
  const conflict = commands(workspace.access, {
    ...emptyClient(),
    continueSession: async () => {
      throw new RequestError(409, { error: "stale continuation" });
    },
    getContinuationReceipt: async () => continuationReceipt(original),
  });

  await conflict.retryOriginalContinuation(sessionId);

  assert.equal(workspace.read().chats[sessionId]?.pendingContinuation, null);
  assert.equal(workspace.read().chats[sessionId]?.knownExecutionId, original.operationId);
});

void test("compaction requires an idle snapshot even when the command is enabled", async () => {
  const workspace = fixture();
  let starts = 0;
  const coordinator = commands(workspace.access, {
    ...emptyClient(),
    startCompaction: async () => {
      starts++;
      return compactionReceipt("unused");
    },
  });

  await coordinator.compact(sessionId, snapshot({ unsettledExecutionId: firstExecutionId }), true);

  assert.equal(starts, 0);
  assert.equal(coordinator.status.compactions[sessionId], undefined);
});

void test("compaction ignores duplicate starts while the first start is pending", async () => {
  const workspace = fixture();
  const start = deferred<CompactionReceipt>();
  const started = deferred<void>();
  let starts = 0;
  let operationId = "";
  const coordinator = commands(workspace.access, {
    ...emptyClient(),
    startCompaction: async (_id, submission) => {
      starts++;
      operationId = submission.operationId;
      started.resolve();
      return start.promise;
    },
  });

  const first = coordinator.compact(sessionId, snapshot(), true);
  await started.promise;
  await coordinator.compact(sessionId, snapshot(), true);
  assert.equal(starts, 1);

  start.resolve(compactionReceipt(operationId));
  await first;
  assert.deepEqual(coordinator.status.compactions[sessionId], {
    phase: "settled",
    message: "History compacted.",
  });
});

void test("a snapshot reads the addressed compaction receipt and reports an unconfirmed no-op", async () => {
  const workspace = fixture();
  const settled = deferred<void>();
  const receiptReads: string[] = [];
  const coordinator = commands(
    workspace.access,
    {
      ...emptyClient(),
      startCompaction: async () => {
        throw new Error("acknowledgement lost");
      },
      getCompactionOperation: async (_id, operationId) => {
        receiptReads.push(operationId);
        return null;
      },
    },
    (state) => {
      if (state.compactions[sessionId]?.phase === "settled") settled.resolve();
    },
  );

  await coordinator.compact(sessionId, snapshot(), true);
  const operation = coordinator.status.compactions[sessionId];
  assert.equal(operation?.phase, "observing");
  if (operation?.phase !== "observing") return;
  coordinator.observeSnapshot(snapshot(), null);
  await settled.promise;

  assert.deepEqual(receiptReads, [operation.operationId]);
  assert.deepEqual(coordinator.status.compactions[sessionId], {
    phase: "settled",
    message: "Compaction was not confirmed.",
  });
  assert.equal(workspace.read().chats[sessionId]?.draft, "submitted text");
});

void test("a transient compaction receipt read recovers on the next snapshot", async () => {
  const workspace = fixture();
  const uncertain = deferred<void>();
  const settled = deferred<void>();
  const receiptReads: string[] = [];
  const coordinator = commands(
    workspace.access,
    {
      ...emptyClient(),
      startCompaction: async () => {
        throw new Error("acknowledgement lost");
      },
      getCompactionOperation: async (_id, operationId) => {
        receiptReads.push(operationId);
        if (receiptReads.length === 1) throw new Error("receipt unavailable");
        return compactionReceipt(operationId);
      },
    },
    (state) => {
      const phase = state.compactions[sessionId]?.phase;
      if (phase === "uncertain") uncertain.resolve();
      if (phase === "settled") settled.resolve();
    },
  );

  await coordinator.compact(sessionId, snapshot(), true);
  const operation = coordinator.status.compactions[sessionId];
  assert.equal(operation?.phase, "observing");
  if (operation?.phase !== "observing") return;
  coordinator.observeSnapshot(snapshot(), null);
  await uncertain.promise;
  coordinator.observeSnapshot(snapshot(), null);
  await settled.promise;

  assert.deepEqual(receiptReads, [operation.operationId, operation.operationId]);
  assert.deepEqual(coordinator.status.compactions[sessionId], {
    phase: "settled",
    message: "History compacted.",
  });
});

void test("pending-input removal reports progress and reads the snapshot after a race", async () => {
  const workspace = fixture();
  const removal = deferred<boolean>();
  let snapshotReads = 0;
  const client = {
    ...emptyClient(),
    removePendingInput: async () => removal.promise,
    getSnapshot: async () => {
      snapshotReads++;
      return snapshot({ unsettledExecutionId: firstExecutionId, pendingInputs: [] });
    },
  };
  const coordinator = commands(workspace.access, client);
  const request = coordinator.removePendingInput(
    sessionId,
    firstExecutionId,
    "queued-submission",
    true,
  );

  assert.equal(
    coordinator.pendingInputRemoval(sessionId, firstExecutionId, "queued-submission")?.status,
    "removing",
  );
  removal.resolve(false);
  await request;

  assert.equal(snapshotReads, 1);
  assert.equal(coordinator.hasPendingInputRemoval(sessionId), false);
});

void test("a removal read begun before Continue acceptance cannot settle the new execution", async () => {
  const workspace = fixture();
  const continuation = deferred<ContinuationReceipt>();
  const read = deferred<SessionSnapshot>();
  const reading = deferred<void>();
  let original: ContinuationSubmission | undefined;
  const coordinator = commands(workspace.access, {
    ...emptyClient(),
    continueSession: async (_id, submission) => {
      original = submission;
      return continuation.promise;
    },
    getSnapshot: async () => {
      reading.resolve();
      return read.promise;
    },
  });
  const continuing = coordinator.continueSession(sessionId, continuableSnapshot(), true);
  const removing = coordinator.removePendingInput(sessionId, firstExecutionId, "queued", true);
  await reading.promise;
  continuation.resolve(continuationReceipt(original!));
  await continuing;
  read.resolve(snapshot());
  await removing;
  assert.equal(workspace.read().chats[sessionId]?.knownExecutionId, original!.operationId);
});

void test("uncertain pending-input removal checks its snapshot without repeating the write", async () => {
  const workspace = fixture();
  let removals = 0;
  let snapshotReads = 0;
  const coordinator = commands(workspace.access, {
    ...emptyClient(),
    removePendingInput: async () => {
      removals++;
      throw new Error("acknowledgement lost");
    },
    getSnapshot: async () => {
      snapshotReads++;
      if (snapshotReads === 1) throw new Error("snapshot unavailable");
      return snapshot({ unsettledExecutionId: firstExecutionId, pendingInputs: [] });
    },
  });
  await coordinator.removePendingInput(sessionId, firstExecutionId, "queued-submission", true);
  assert.equal(
    coordinator.pendingInputRemoval(sessionId, firstExecutionId, "queued-submission")?.status,
    "uncertain",
  );
  await coordinator.checkPendingInputRemoval(sessionId, firstExecutionId, "queued-submission");

  assert.equal(removals, 1);
  assert.equal(snapshotReads, 2);
  assert.equal(coordinator.hasPendingInputRemoval(sessionId), false);
});

void test("a definite Continue conflict without a receipt clears uncertainty", async () => {
  const workspace = fixture();
  const coordinator = commands(workspace.access, {
    ...emptyClient(),
    continueSession: async () => {
      throw new RequestError(409, { error: "stale continuation" });
    },
    getContinuationReceipt: async () => null,
  });

  await coordinator.continueSession(sessionId, continuableSnapshot(), true);

  assert.equal(workspace.read().chats[sessionId]?.pendingContinuation, null);
  assert.match(workspace.read().chats[sessionId]?.error ?? "", /stale continuation/);
});

void test("an accepted not-sent resend gets a new ID and can be explicitly repeated", async () => {
  const workspace = fixture();
  const source: UserMessageSubmission = {
    submissionId: "30000000-0000-0000-0000-000000000099",
    expectedHistoryRevision: 4,
    text: "submitted text",
    llmSelection: selection,
  };
  const dispatched: UserMessageSubmission[] = [];
  let journaledBeforeDispatch = true;
  const coordinator = commands(
    workspace.access,
    emptyClient(async (_id, submission) => {
      dispatched.push(submission);
      journaledBeforeDispatch &&=
        workspace.durable().chats[sessionId]?.pending?.preserveDraft === true;
      return receipt(submission);
    }),
  );

  const first = await coordinator.resendNotSent(
    sessionId,
    snapshot({ historyRevision: 12 }),
    source,
  );

  assert.equal(first?.receipt?.acceptance, "accepted");
  assert.equal(first?.submissionId, dispatched[0]?.submissionId);
  assert.notEqual(dispatched[0]?.submissionId, source.submissionId);
  assert.equal(dispatched[0]?.expectedHistoryRevision, 12);
  assert.equal(dispatched[0]?.text, source.text);
  assert.deepEqual(dispatched[0]?.llmSelection, source.llmSelection);
  assert.equal(journaledBeforeDispatch, true);
  assert.equal(workspace.read().chats[sessionId]?.draft, "submitted text");
  assert.equal(workspace.read().chats[sessionId]?.pending, null);

  workspace.reload();
  const second = await coordinator.resendNotSent(
    sessionId,
    snapshot({ historyRevision: 13 }),
    source,
  );
  assert.equal(second?.receipt?.acceptance, "accepted");
  assert.equal(second?.submissionId, dispatched[1]?.submissionId);
  assert.notEqual(dispatched[1]?.submissionId, dispatched[0]?.submissionId);
  assert.equal(dispatched[1]?.expectedHistoryRevision, 13);
  assert.equal(dispatched.length, 2);
  assert.equal(workspace.read().chats[sessionId]?.draft, "submitted text");
});

void test("an unacknowledged resend remains uncertain after an unrelated Send succeeds", async () => {
  const workspace = fixture();
  const source: UserMessageSubmission = {
    submissionId: "30000000-0000-0000-0000-000000000097",
    expectedHistoryRevision: 3,
    text: "submitted text",
    llmSelection: selection,
  };
  const coordinator = commands(
    workspace.access,
    emptyClient(async () => {
      throw new Error("acknowledgement lost");
    }),
  );

  const uncertain = await coordinator.resendNotSent(sessionId, snapshot(), source);
  assert.equal(uncertain?.receipt, null);
  assert.equal(workspace.read().chats[sessionId]?.pending?.status, "uncertain");
  workspace.access.updateChat(sessionId, (previous) => ({ ...previous, pending: null }));

  const unrelatedSend = commands(
    workspace.access,
    emptyClient(async (_id, submission) => ({
      ...receipt(submission),
      executionId: nextExecutionId,
    })),
  );
  await unrelatedSend.send(sessionId, snapshot(), false, true);
  assert.equal(workspace.read().chats[sessionId]?.knownExecutionId, nextExecutionId);
  assert.equal(workspace.read().chats[sessionId]?.pending, null);
});

void test("uncertain not-sent resend reloads with its new ID and reconciles the exact receipt", async () => {
  const workspace = fixture();
  const source: UserMessageSubmission = {
    submissionId: "30000000-0000-0000-0000-000000000098",
    expectedHistoryRevision: 3,
    text: "submitted text",
    llmSelection: selection,
  };
  const dispatched: UserMessageSubmission[] = [];
  const first = commands(
    workspace.access,
    emptyClient(async (_id, submission) => {
      dispatched.push(submission);
      throw new Error("acknowledgement lost");
    }),
  );

  const uncertain = await first.resendNotSent(sessionId, snapshot(), source);
  assert.equal(uncertain?.receipt, null);
  assert.notEqual(uncertain?.submissionId, source.submissionId);
  workspace.reload();
  const recoveredSubmission = workspace.read().chats[sessionId]?.pending?.original;
  assert.ok(recoveredSubmission);
  assert.equal(recoveredSubmission.submissionId, uncertain?.submissionId);
  assert.deepEqual(recoveredSubmission, dispatched[0]);
  assert.equal(workspace.read().chats[sessionId]?.pending?.status, "uncertain");
  assert.equal(workspace.read().chats[sessionId]?.draft, "submitted text");

  const receiptReads: string[] = [];
  const recovery = commands(workspace.access, {
    ...emptyClient(async () => {
      throw new Error("uncertain resend must not be automatically dispatched again");
    }),
    getMessageReceipt: async (_id, submissionId) => {
      receiptReads.push(submissionId);
      return submissionId === recoveredSubmission.submissionId
        ? receipt(recoveredSubmission)
        : null;
    },
  });
  assert.equal(await recovery.resendNotSent(sessionId, snapshot(), source), null);
  const reconciled = await withVisibleDocument(() => recovery.checkReceipt(sessionId));
  assert.equal(reconciled?.acceptance, "accepted");
  assert.deepEqual(receiptReads, [recoveredSubmission.submissionId]);
  assert.equal(dispatched.length, 1);
  assert.equal(workspace.read().chats[sessionId]?.pending, null);
  assert.equal(workspace.read().chats[sessionId]?.knownExecutionId, firstExecutionId);
  assert.equal(workspace.read().chats[sessionId]?.draft, "submitted text");
});

void test("a late Stop failure for an earlier execution cannot replace or block Stop for its successor", async () => {
  const workspace = fixture();
  const first = deferred<StopReceipt>();
  const second = deferred<StopReceipt>();
  const third = deferred<StopReceipt>();
  const executions: string[] = [];
  const coordinator = commands(
    workspace.access,
    emptyClient(undefined, async (_id, executionId) => {
      executions.push(executionId);
      if (executions.length === 1) return first.promise;
      if (executions.length === 2) return second.promise;
      return third.promise;
    }),
  );

  const stopFirst = coordinator.stop(
    sessionId,
    snapshot({ unsettledExecutionId: firstExecutionId }),
    undefined,
  );
  coordinator.observeSnapshot(
    snapshot({
      unsettledExecutionId: nextExecutionId,
      lastSettlement: { executionId: firstExecutionId, outcome: "completed" },
    }),
    null,
  );
  const stopSecond = coordinator.stop(
    sessionId,
    snapshot({ unsettledExecutionId: nextExecutionId }),
    undefined,
  );
  second.reject(new Error("acknowledgement lost"));
  await stopSecond;
  assert.equal(coordinator.status.stopping[sessionId]?.executionId, nextExecutionId);
  assert.equal(coordinator.status.stopping[sessionId]?.pending, false);

  first.reject(new Error("older acknowledgement lost"));
  await stopFirst;
  assert.equal(coordinator.status.stopping[sessionId]?.executionId, nextExecutionId);
  assert.match(coordinator.status.stopping[sessionId]?.error ?? "", /acknowledgement lost/);

  const retrySecond = coordinator.stop(
    sessionId,
    snapshot({ unsettledExecutionId: nextExecutionId }),
    undefined,
  );
  assert.deepEqual(executions, [firstExecutionId, nextExecutionId, nextExecutionId]);
  third.resolve({ executionId: nextExecutionId, status: "requested", outcome: null });
  await retrySecond;
});
