import assert from "node:assert/strict";
import test from "node:test";
import { appendDraftImage, removeDraftImage, editDraft } from "../../chat/ChatState.ts";
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

void test("image sends journal ordered refs and preserve newer text and image edits", async () => {
  const workspace = fixture();
  const first = { imageId: "first", width: 10, height: 20 };
  const second = { imageId: "second", width: 20, height: 30 };
  workspace.access.updateChat(sessionId, (chat) =>
    appendDraftImage(
      appendDraftImage({ ...chat, selection: { ...selection, supportsImages: true } }, first),
      second,
    ),
  );
  const coordinator = commands(
    workspace.access,
    emptyClient(async (_id, original) => {
      assert.deepEqual(original.imageIds, ["first", "second"]);
      assert.deepEqual(
        workspace.durable().chats[sessionId]?.pending?.original.imageIds,
        original.imageIds,
      );
      workspace.access.updateChat(sessionId, (chat) =>
        appendDraftImage(removeDraftImage(editDraft(chat, "new"), "first"), {
          imageId: "third",
          width: 1,
          height: 1,
        }),
      );
      return receipt(original);
    }),
  );
  await coordinator.send(sessionId, snapshot(), false, true);
  assert.equal(workspace.read().chats[sessionId]?.draft, "new");
  assert.deepEqual(
    workspace.read().chats[sessionId]?.draftImages.map((image) => image.imageId),
    ["second", "third"],
  );
});

void test("image-only send retries the same journal after reload", async () => {
  const workspace = fixture();
  workspace.access.updateChat(sessionId, (chat) =>
    appendDraftImage(
      editDraft({ ...chat, selection: { ...selection, supportsImages: true } }, ""),
      { imageId: "image", width: 4, height: 5 },
    ),
  );
  const first = commands(
    workspace.access,
    emptyClient(async () => {
      throw new Error("lost");
    }),
  );
  await first.send(sessionId, snapshot(), false, true);
  workspace.reload();
  const original = workspace.read().chats[sessionId]!.pending!.original;
  assert.deepEqual(original.imageIds, ["image"]);
  const retry = commands(
    workspace.access,
    emptyClient(async (_id, submission) => {
      assert.deepEqual(submission, original);
      return receipt(submission);
    }),
  );
  await retry.retryOriginalSubmission(sessionId);
  assert.deepEqual(workspace.read().chats[sessionId]?.draftImages, []);
});

void test("a text-only selection cannot dispatch a draft with images", async () => {
  const { access, read } = fixture();
  const image = { imageId: "retained", width: 4, height: 5 };
  access.updateChat(sessionId, (chat) => appendDraftImage({ ...chat, selection }, image));
  let sends = 0;
  const coordinator = commands(
    access,
    emptyClient(async (_id, submission) => {
      sends++;
      return receipt(submission);
    }),
  );
  await coordinator.send(sessionId, snapshot(), false, true);
  assert.equal(sends, 0);
  assert.deepEqual(read().chats[sessionId]?.draftImages, [image]);
  assert.equal(read().chats[sessionId]?.pending, null);
});

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
