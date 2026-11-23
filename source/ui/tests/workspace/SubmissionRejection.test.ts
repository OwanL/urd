import assert from "node:assert/strict";
import test from "node:test";
import { appendDraftImage, editDraft } from "../../chat/ChatState.ts";
import { RequestError } from "../../http/RequestError.ts";
import {
  commands,
  emptyClient,
  fixture,
  receipt,
  selection,
  sessionId,
  snapshot,
} from "./WorkspaceCommandsFixture.ts";

void test("a rejected first image submission unlocks the unchanged draft", async () => {
  const workspace = fixture();
  const image = { imageId: "image", width: 4, height: 5 };
  workspace.access.updateChat(sessionId, (chat) =>
    appendDraftImage({ ...chat, selection: { ...selection, supportsImages: true } }, image),
  );
  const coordinator = commands(
    workspace.access,
    emptyClient(async () => {
      workspace.access.updateChat(sessionId, (chat) => editDraft(chat, "newer text"));
      throw new RequestError(400, { error: "Invalid image." });
    }),
  );
  await coordinator.send(sessionId, snapshot(), false, true);
  const chat = workspace.reload().chats[sessionId]!;
  assert.equal(chat.pending, null);
  assert.equal(chat.draft, "newer text");
  assert.deepEqual(chat.draftImages, [image]);
  assert.match(chat.error!, /Invalid image/);
});

void test("a 400 on retry does not discard an earlier uncertain send", async () => {
  const workspace = fixture();
  await commands(
    workspace.access,
    emptyClient(async () => {
      throw new Error("lost");
    }),
  ).send(sessionId, snapshot(), false, true);
  const original = workspace.read().chats[sessionId]!.pending!.original;
  const retry = commands(workspace.access, {
    ...emptyClient(async () => {
      throw new RequestError(400, null);
    }),
    getMessageReceipt: async () => null,
  });
  await retry.retryOriginalSubmission(sessionId);
  assert.deepEqual(workspace.read().chats[sessionId]?.pending?.original, original);
  assert.equal(workspace.read().chats[sessionId]?.pending?.status, "uncertain");
});

void test("a 400 retry resolves an earlier accepted receipt", async () => {
  const workspace = fixture();
  await commands(
    workspace.access,
    emptyClient(async () => {
      throw new Error("lost");
    }),
  ).send(sessionId, snapshot(), false, true);
  const original = workspace.read().chats[sessionId]!.pending!.original;
  const retry = commands(workspace.access, {
    ...emptyClient(async () => {
      throw new RequestError(400, null);
    }),
    getMessageReceipt: async () => receipt(original),
  });
  await retry.retryOriginalSubmission(sessionId);
  assert.equal(workspace.read().chats[sessionId]?.pending, null);
  assert.equal(workspace.read().chats[sessionId]?.draft, "");
});
