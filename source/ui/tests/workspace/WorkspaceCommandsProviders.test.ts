import assert from "node:assert/strict";
import test from "node:test";
import {
  sessionId,
  snapshot,
  fixture,
  commands,
  continuableSnapshot,
  receipt,
  continuationReceipt,
  emptyClient,
  deferred,
} from "./WorkspaceCommandsFixture.ts";

void test("Send and Continue do not dispatch while this session's provider change is pending", async () => {
  const workspace = fixture();
  const save = deferred<{ connectionIds: readonly string[] | null }>();
  const dispatched: string[] = [];
  const coordinator = commands(workspace.access, {
    ...emptyClient(async (_id, submission) => {
      dispatched.push("send");
      return receipt(submission);
    }),
    continueSession: async (_id, submission) => {
      dispatched.push("continue");
      return continuationReceipt(submission);
    },
    setSubagentProviders: () => save.promise,
  });

  const saving = coordinator.saveSubagentProviders(sessionId, []);
  await coordinator.send(sessionId, snapshot(), false, true);
  await coordinator.continueSession(sessionId, continuableSnapshot(), true);
  assert.deepEqual(dispatched, []);
  assert.equal(workspace.read().chats[sessionId]?.draft, "submitted text");

  save.resolve({ connectionIds: [] });
  await saving;
  await coordinator.continueSession(sessionId, continuableSnapshot(), true);
  assert.deepEqual(dispatched, ["continue"]);
});
