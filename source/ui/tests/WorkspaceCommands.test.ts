import assert from "node:assert/strict";
import test from "node:test";
import { WorkspaceCommands } from "../app/WorkspaceCommands.ts";
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
import type { SessionSnapshot } from "../sessions/SessionSnapshot.ts";
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
  reasoningEffort: null,
};

function snapshot(overrides: Partial<SessionSnapshot> = {}): SessionSnapshot {
  return {
    sessionId,
    historyRevision: 7,
    admissionFenced: false,
    unsettledExecutionId: null,
    stopRequested: false,
    lastSettlement: null,
    latestLlmSelection: selection,
    firstUserPreview: null,
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
  client: Pick<SessionClient, "create" | "submitMessage" | "getMessageReceipt" | "stopExecution">,
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

function receipt(submission: UserMessageSubmission): UserMessageReceipt {
  return {
    sessionId,
    submission,
    acceptance: "accepted",
    refusalReason: null,
    executionId: firstExecutionId,
  };
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
): Pick<SessionClient, "create" | "submitMessage" | "getMessageReceipt" | "stopExecution"> {
  return {
    create: async () => sessionId,
    submitMessage,
    getMessageReceipt: async () => null,
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

  await retry.reconcile(sessionId, true);

  assert.deepEqual(dispatched, original);
  assert.equal(journaledBeforeDispatch, true);
  assert.equal(workspace.read().chats[sessionId]?.pending, null);
  assert.equal(workspace.read().chats[sessionId]?.draft, "newer draft");
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
  const reconciled = await withVisibleDocument(() => recovery.reconcile(sessionId, false));
  assert.equal(reconciled?.acceptance, "accepted");
  assert.deepEqual(receiptReads, [recoveredSubmission.submissionId]);
  assert.equal(dispatched.length, 1);
  assert.equal(workspace.read().chats[sessionId]?.pending, null);
  assert.equal(workspace.read().chats[sessionId]?.knownExecutionId, firstExecutionId);
  assert.equal(workspace.read().chats[sessionId]?.draft, "submitted text");
});

void test("session metadata, not Send dispatch, names a new chat", async () => {
  const workspace = fixture();
  const coordinator = commands(
    workspace.access,
    emptyClient(async (_id, submission) => receipt(submission)),
  );

  await coordinator.send(sessionId, snapshot(), false, true);
  assert.equal(workspace.read().chats[sessionId]?.title, "New chat");

  coordinator.observeSnapshot(
    snapshot({ firstUserPreview: "  First user prompt is authoritative  " }),
    null,
  );
  assert.equal(workspace.read().chats[sessionId]?.title, "First user prompt is authoritative");
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
