import assert from "node:assert/strict";
import test from "node:test";
import { RequestError } from "../../http/RequestError.ts";
import { commands, emptyClient, fixture, sessionId, snapshot } from "./WorkspaceCommandsFixture.ts";

void test("a definitive first rejection unlocks the draft without a receipt read", async () => {
  const workspace = fixture();
  let reads = 0;
  const coordinator = commands(workspace.access, {
    ...emptyClient(async () => {
      throw new RequestError(400, null);
    }),
    getMessageReceipt: async () => {
      reads++;
      throw new Error("offline");
    },
  });
  await coordinator.send(sessionId, snapshot(), false, true);
  assert.equal(reads, 0);
  assert.equal(workspace.reload().chats[sessionId]?.pending, null);
  assert.match(workspace.read().chats[sessionId]!.error!, /rejected/);
});
