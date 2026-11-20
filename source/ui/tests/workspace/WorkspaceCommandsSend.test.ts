import assert from "node:assert/strict";
import test from "node:test";
import { editDraft } from "../../chat/ChatState.ts";
import type { UserMessageSubmission } from "../../sessions/inputs/UserMessageSubmission.ts";
import {
  sessionId,
  firstExecutionId,
  nextExecutionId,
  selection,
  snapshot,
  fixture,
  commands,
  receipt,
  emptyClient,
  withVisibleDocument,
} from "./WorkspaceCommandsFixture.ts";

void test("Send freezes the inherited model and remembers it for subsequent empty chats", async () => {
  const { access, read, durable } = fixture();
  access.updateChat(sessionId, (chat) => ({ ...chat, selection: null }));
  const fallback = { ...selection, modelId: "restored-model" };
  let sends = 0;
  const client = emptyClient(async (_id, original) => {
    sends++;
    assert.deepEqual(original.llmSelection, fallback);
    assert.deepEqual(durable().chats[sessionId]?.pending?.original.llmSelection, fallback);
    return receipt(original);
  });

  await commands(access, client).send(
    sessionId,
    snapshot({ latestLlmSelection: null }),
    false,
    true,
    fallback,
  );

  assert.equal(sends, 1);
  assert.deepEqual(read().lastSelection, fallback);
  assert.equal(read().chats[sessionId]?.selection, null);
});

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

  await retry.retryOriginalSubmission(sessionId);

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
