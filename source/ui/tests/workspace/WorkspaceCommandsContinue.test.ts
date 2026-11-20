import assert from "node:assert/strict";
import test from "node:test";
import { RequestError } from "../../http/RequestError.ts";
import { editDraft } from "../../chat/ChatState.ts";
import type { ContinuationSubmission } from "../../sessions/inputs/Continuation.ts";
import {
  sessionId,
  firstExecutionId,
  selection,
  fixture,
  commands,
  continuableSnapshot,
  continuationReceipt,
  emptyClient,
  withVisibleDocument,
} from "./WorkspaceCommandsFixture.ts";

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
