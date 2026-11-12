import assert from "node:assert/strict";
import test from "node:test";
import { MessageEditing } from "../app/MessageEditing.ts";
import {
  decodeWorkspace,
  emptyWorkspace,
  encodeWorkspace,
  newChat,
} from "../app/WorkspaceState.ts";
import type { WorkspaceCommandAccess } from "../app/WorkspaceCommands.ts";
import type { LlmSelection } from "../providers/LlmSelection.ts";
import type { MessageEditSubmission } from "../sessions/MessageEdit.ts";
import type { TranscriptEntryRef, TranscriptTextFacts } from "../sessions/Messages.ts";
import type { SessionClient } from "../sessions/SessionClient.ts";

const selection: LlmSelection = {
  connection: {
    id: "provider",
    name: "Test",
    route: "custom",
    api: "openAIResponses",
    endpoint: "http://127.0.0.1:1/v1/",
    authentication: "none",
  },
  modelId: "current-model",
  family: "other",
  supportsImages: false,
  contextWindow: null,
  reasoningEffort: "high",
};
const target: TranscriptEntryRef = {
  executionId: "old-run",
  entryId: "old-message",
  kind: "user",
  historyRevision: 3,
};
const cursor = {
  message: {
    executionId: target.executionId,
    messageId: target.entryId,
    role: "user" as const,
    historyRevision: 3,
  },
  segmentIndex: 0,
  utf8Offset: 0,
};
const facts: TranscriptTextFacts = {
  from: cursor,
  availableThrough: { ...cursor, segmentIndex: 1 },
  isClosed: true,
  finalOutputCommitted: false,
};

function fixture() {
  let state = emptyWorkspace();
  state.tabs = ["session"];
  state.activeId = "session";
  state.chats.session = { ...newChat(selection), draft: "untouched composer draft" };
  let saved = encodeWorkspace(state);
  let writable = true;
  const access: WorkspaceCommandAccess = {
    read: () => state,
    save: (next) => {
      state = next;
      if (!writable) return false;
      saved = encodeWorkspace(state);
      return true;
    },
    updateChat: (id, update) =>
      access.save({ ...state, chats: { ...state.chats, [id]: update(state.chats[id]!) } }),
    canPersist: () => true,
  };
  const client: Pick<SessionClient, "getMessageContent" | "editMessage" | "getMessageEditReceipt"> =
    {
      getMessageContent: async (_id, from) => ({
        from,
        text: "original 雪🙂",
        next: facts.availableThrough,
        availableThrough: facts.availableThrough,
        hasMore: false,
        isClosed: true,
        finalOutputCommitted: false,
      }),
      editMessage: async (_id, command) => ({
        sessionId: "session",
        operationId: command.operationId,
        historyRevision: 4,
        executionId: "new-run",
      }),
      getMessageEditReceipt: async () => null,
    };
  return {
    access,
    client,
    read: () => state.chats.session!,
    reload: () => {
      state = decodeWorkspace(saved);
    },
    setWritable: (value: boolean) => {
      writable = value;
    },
    controller: () => new MessageEditing(client, access, () => {}),
  };
}

void test("opening reads every page, cancel preserves history and composer draft, and reload retains an edit", async () => {
  const f = fixture();
  let reads = 0;
  f.client.getMessageContent = async (_id, from) => {
    reads++;
    const next = reads === 1 ? { ...cursor, utf8Offset: 4 } : facts.availableThrough;
    return {
      from: { utf8Offset: from.utf8Offset, segmentIndex: from.segmentIndex, message: from.message },
      text: reads === 1 ? "one " : "雪🙂",
      next,
      availableThrough: facts.availableThrough,
      hasMore: reads === 1,
      isClosed: true,
      finalOutputCommitted: false,
    };
  };
  const controller = f.controller();
  await controller.open("session", target, facts);
  assert.equal(reads, 2);
  assert.equal(f.read().messageEdit?.text, "one 雪🙂");
  controller.change("session", "changed text");
  f.reload();
  assert.equal(f.read().messageEdit?.text, "changed text");
  controller.cancel("session");
  assert.equal(f.read().messageEdit, undefined);
  assert.equal(f.read().draft, "untouched composer draft");
  assert.equal(f.read().knownExecutionId, null);
});

void test("cancelling an unfinished message read aborts it without restoring the editor", async () => {
  const f = fixture();
  let observed: AbortSignal | undefined;
  f.client.getMessageContent = async (_id, _from, signal) => {
    observed = signal;
    return new Promise((_resolve, reject) =>
      signal?.addEventListener("abort", () => reject(signal.reason), { once: true }),
    );
  };
  const controller = f.controller();
  const opening = controller.open("session", target, facts);
  assert.equal(f.read().messageEdit?.status, "loading");
  controller.cancel("session");
  await opening;
  assert.equal(observed?.aborted, true);
  assert.equal(f.read().messageEdit, undefined);
  assert.equal(f.read().draft, "untouched composer draft");
});

void test("uncertain Save freezes text/model and reload reconciles its receipt without another edit", async () => {
  const f = fixture();
  let dispatched: MessageEditSubmission | undefined;
  let sends = 0;
  f.client.editMessage = async (_id, original) => {
    sends++;
    dispatched = original;
    assert.equal(f.read().messageEdit?.submission?.operationId, original.operationId);
    throw new Error("Lost acknowledgement");
  };
  const controller = f.controller();
  await controller.open("session", target, facts);
  controller.change("session", "  replacement 雪🙂\0  ");
  await controller.save("session", selection);
  assert.equal(f.read().messageEdit?.status, "uncertain");
  assert.equal(dispatched?.llmSelection.modelId, selection.modelId);
  assert.equal(dispatched?.llmSelection.reasoningEffort, "high");
  assert.notEqual(dispatched?.llmSelection, selection);
  controller.change("session", "must not replace an uncertain operation");
  controller.cancel("session");
  assert.equal(f.read().messageEdit?.text, dispatched?.text);
  f.reload();
  assert.equal(f.read().messageEdit?.submission?.operationId, dispatched?.operationId);
  f.client.getMessageEditReceipt = async (_id, operationId) => ({
    sessionId: "session",
    operationId,
    historyRevision: 4,
    executionId: "new-run",
  });
  await f.controller().reconcile("session");
  assert.equal(sends, 1);
  assert.equal(f.read().messageEdit, undefined);
  assert.equal(f.read().knownExecutionId, "new-run");
  assert.equal(f.read().draft, "untouched composer draft");
});

void test("failed journals dispatch nothing and an explicit retry reuses the original Save", async () => {
  const f = fixture();
  const controller = f.controller();
  await controller.open("session", target, facts);
  let sends = 0;
  let first: MessageEditSubmission | undefined;
  f.client.editMessage = async (_id, command) => {
    sends++;
    if (!first) {
      first = structuredClone(command);
      throw new Error("Lost response");
    }
    assert.deepEqual(command, first);
    return {
      sessionId: "session",
      operationId: command.operationId,
      historyRevision: 4,
      executionId: "new-run",
    };
  };
  controller.change("session", "replacement");
  f.setWritable(false);
  await controller.save("session", selection);
  assert.equal(sends, 0);
  assert.equal(f.read().messageEdit?.status, "editing");
  assert.equal(f.read().messageEdit?.text, "replacement");
  f.setWritable(true);
  await controller.save("session", selection);
  assert.equal(sends, 1);
  await controller.reconcile("session");
  assert.equal(f.read().messageEdit?.status, "uncertain");
  await Promise.all([controller.reconcile("session", true), controller.reconcile("session", true)]);
  assert.equal(sends, 2);
  assert.equal(f.read().knownExecutionId, "new-run");
});
