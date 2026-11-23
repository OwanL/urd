import assert from "node:assert/strict";
import test from "node:test";
import {
  commands,
  emptyClient,
  fixture,
  selection,
  sessionId,
  snapshot,
} from "./WorkspaceCommandsFixture.ts";

void test("oversized image drafts are not journaled or sent", async () => {
  const workspace = fixture();
  const images = Array.from({ length: 9 }, (_, i) => ({ imageId: String(i), width: 1, height: 1 }));
  workspace.access.updateChat(sessionId, (chat) => ({
    ...chat,
    selection: { ...selection, supportsImages: true },
    draftImages: images,
  }));
  let sent = false;
  await commands(
    workspace.access,
    emptyClient(async () => {
      sent = true;
      throw new Error("unexpected send");
    }),
  ).send(sessionId, snapshot(), false, true);
  assert.equal(sent, false);
  assert.equal(workspace.read().chats[sessionId]?.pending, null);
});
