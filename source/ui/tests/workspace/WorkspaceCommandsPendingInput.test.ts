import assert from "node:assert/strict";
import test from "node:test";
import type {
  ContinuationSubmission,
  ContinuationReceipt,
} from "../../sessions/inputs/Continuation.ts";
import type { SessionSnapshot } from "../../sessions/SessionSnapshot.ts";
import {
  sessionId,
  firstExecutionId,
  snapshot,
  fixture,
  commands,
  continuableSnapshot,
  continuationReceipt,
  emptyClient,
  deferred,
} from "./WorkspaceCommandsFixture.ts";

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
