import assert from "node:assert/strict";
import test from "node:test";
import type { CompactionReceipt } from "../../sessions/inputs/Compaction.ts";
import {
  sessionId,
  firstExecutionId,
  snapshot,
  fixture,
  commands,
  compactionReceipt,
  emptyClient,
  deferred,
} from "./WorkspaceCommandsFixture.ts";

void test("compaction requires an idle snapshot even when the command is enabled", async () => {
  const workspace = fixture();
  let starts = 0;
  const coordinator = commands(workspace.access, {
    ...emptyClient(),
    startCompaction: async () => {
      starts++;
      return compactionReceipt("unused");
    },
  });

  await coordinator.compact(sessionId, snapshot({ unsettledExecutionId: firstExecutionId }), true);

  assert.equal(starts, 0);
  assert.equal(coordinator.status.compactions[sessionId], undefined);
});

void test("compaction ignores duplicate starts while the first start is pending", async () => {
  const workspace = fixture();
  const start = deferred<CompactionReceipt>();
  const started = deferred<void>();
  let starts = 0;
  let operationId = "";
  const coordinator = commands(workspace.access, {
    ...emptyClient(),
    startCompaction: async (_id, submission) => {
      starts++;
      operationId = submission.operationId;
      started.resolve();
      return start.promise;
    },
  });

  const first = coordinator.compact(sessionId, snapshot(), true);
  await started.promise;
  await coordinator.compact(sessionId, snapshot(), true);
  assert.equal(starts, 1);

  start.resolve(compactionReceipt(operationId));
  await first;
  assert.deepEqual(coordinator.status.compactions[sessionId], {
    phase: "settled",
    message: "History compacted.",
  });
});

void test("manual compaction displays a failed receipt error when provided", async () => {
  const workspace = fixture();
  const coordinator = commands(workspace.access, {
    ...emptyClient(),
    startCompaction: async (_id, submission) => ({
      ...compactionReceipt(submission.operationId, "failed"),
      error: "Compaction failed: provider unavailable.",
    }),
  });

  await coordinator.compact(sessionId, snapshot(), true);

  assert.deepEqual(coordinator.status.compactions[sessionId], {
    phase: "settled",
    message: "Compaction failed: provider unavailable.",
  });
});

void test("manual compaction keeps the fallback for a failed receipt without an error", async () => {
  const workspace = fixture();
  const coordinator = commands(workspace.access, {
    ...emptyClient(),
    startCompaction: async (_id, submission) => compactionReceipt(submission.operationId, "failed"),
  });

  await coordinator.compact(sessionId, snapshot(), true);

  assert.deepEqual(coordinator.status.compactions[sessionId], {
    phase: "settled",
    message: "Compaction failed. Previous context kept.",
  });
});

void test("a snapshot reads the addressed compaction receipt and reports an unconfirmed no-op", async () => {
  const workspace = fixture();
  const settled = deferred<void>();
  const receiptReads: string[] = [];
  const coordinator = commands(
    workspace.access,
    {
      ...emptyClient(),
      startCompaction: async () => {
        throw new Error("acknowledgement lost");
      },
      getCompactionOperation: async (_id, operationId) => {
        receiptReads.push(operationId);
        return null;
      },
    },
    (state) => {
      if (state.compactions[sessionId]?.phase === "settled") settled.resolve();
    },
  );

  await coordinator.compact(sessionId, snapshot(), true);
  const operation = coordinator.status.compactions[sessionId];
  assert.equal(operation?.phase, "observing");
  if (operation?.phase !== "observing") return;
  coordinator.observeSnapshot(snapshot(), null);
  await settled.promise;

  assert.deepEqual(receiptReads, [operation.operationId]);
  assert.deepEqual(coordinator.status.compactions[sessionId], {
    phase: "settled",
    message: "Compaction was not confirmed.",
  });
  assert.equal(workspace.read().chats[sessionId]?.draft, "submitted text");
});

void test("a transient compaction receipt read recovers on the next snapshot", async () => {
  const workspace = fixture();
  const uncertain = deferred<void>();
  const settled = deferred<void>();
  const receiptReads: string[] = [];
  const coordinator = commands(
    workspace.access,
    {
      ...emptyClient(),
      startCompaction: async () => {
        throw new Error("acknowledgement lost");
      },
      getCompactionOperation: async (_id, operationId) => {
        receiptReads.push(operationId);
        if (receiptReads.length === 1) throw new Error("receipt unavailable");
        return compactionReceipt(operationId);
      },
    },
    (state) => {
      const phase = state.compactions[sessionId]?.phase;
      if (phase === "uncertain") uncertain.resolve();
      if (phase === "settled") settled.resolve();
    },
  );

  await coordinator.compact(sessionId, snapshot(), true);
  const operation = coordinator.status.compactions[sessionId];
  assert.equal(operation?.phase, "observing");
  if (operation?.phase !== "observing") return;
  coordinator.observeSnapshot(snapshot(), null);
  await uncertain.promise;
  coordinator.observeSnapshot(snapshot(), null);
  await settled.promise;

  assert.deepEqual(receiptReads, [operation.operationId, operation.operationId]);
  assert.deepEqual(coordinator.status.compactions[sessionId], {
    phase: "settled",
    message: "History compacted.",
  });
});
