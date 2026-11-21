import assert from "node:assert/strict";
import test from "node:test";
import type { CompactionReceipt } from "../../sessions/inputs/Compaction.ts";
import { compactionStatus } from "../../chat/ChatCommands.ts";
import {
  sessionId,
  firstExecutionId,
  snapshot,
  fixture,
  commands,
  compactionReceipt,
  receipt,
  emptyClient,
  deferred,
} from "./WorkspaceCommandsFixture.ts";

void test("cancelling a snapshot queue is deduplicated and ignores stale snapshots", async () => {
  const workspace = fixture();
  const pending = deferred<CompactionReceipt>();
  let cancellations = 0;
  const coordinator = commands(workspace.access, {
    ...emptyClient(),
    cancelCompaction: async (_id, operationId) => {
      cancellations++;
      assert.equal(operationId, firstExecutionId);
      return pending.promise;
    },
  });
  const queued = snapshot({ queuedCompactionId: firstExecutionId });
  const first = coordinator.cancelCompaction(sessionId, queued);
  await coordinator.cancelCompaction(sessionId, queued);
  assert.equal(cancellations, 1);
  pending.resolve(compactionReceipt(firstExecutionId, "cancelled"));
  await first;
  coordinator.observeSnapshot(queued, null);
  assert.equal(compactionStatus(queued, coordinator.status.compactions[sessionId]), null);
  await coordinator.cancelCompaction(sessionId, queued);
  assert.equal(cancellations, 1);
});

void test("an older receipt read cannot overwrite successful cancellation", async () => {
  const workspace = fixture();
  const read = deferred<CompactionReceipt>();
  const coordinator = commands(workspace.access, {
    ...emptyClient(),
    getCompactionOperation: async () => read.promise,
    cancelCompaction: async (_id, operationId) => compactionReceipt(operationId, "cancelled"),
  });
  const queued = snapshot({ queuedCompactionId: firstExecutionId });
  coordinator.observeSnapshot(queued, null);
  coordinator.observeSnapshot(snapshot(), null);
  await coordinator.cancelCompaction(sessionId, queued);
  read.resolve({ ...compactionReceipt(firstExecutionId, null), queued: true });
  await Promise.resolve();
  await Promise.resolve();
  assert.equal(compactionStatus(queued, coordinator.status.compactions[sessionId]), null);
});

void test("failed cancellation reconciles a queued receipt and permits retry", async () => {
  const workspace = fixture();
  let cancellations = 0;
  const reconciled = deferred<void>();
  const coordinator = commands(
    workspace.access,
    {
      ...emptyClient(),
      cancelCompaction: async () => {
        cancellations++;
        throw new Error("offline");
      },
      getCompactionOperation: async (_id, operationId) => ({
        ...compactionReceipt(operationId, null),
        queued: true,
      }),
    },
    (state) => {
      const operation = state.compactions[sessionId];
      if (operation?.phase === "observing" && operation.queued && !operation.cancelling)
        reconciled.resolve();
    },
  );
  const queued = snapshot({ queuedCompactionId: firstExecutionId });
  await coordinator.cancelCompaction(sessionId, queued);
  await reconciled.promise;
  await coordinator.cancelCompaction(sessionId, queued);
  assert.equal(cancellations, 2);
});

void test("compaction can be queued during an execution when the server permits it", async () => {
  const workspace = fixture();
  let starts = 0;
  const coordinator = commands(workspace.access, {
    ...emptyClient(),
    startCompaction: async (_id, submission) => {
      starts++;
      return { ...compactionReceipt(submission.operationId, null), queued: true };
    },
  });

  await coordinator.compact(sessionId, snapshot({ unsettledExecutionId: firstExecutionId }), true);

  assert.equal(starts, 1);
  const operation = coordinator.status.compactions[sessionId];
  assert.equal(operation?.phase, "observing");
  if (operation?.phase !== "observing") return;
  assert.equal(operation.queued, true);
  assert.equal(
    compactionStatus(snapshot({ unsettledExecutionId: firstExecutionId }), operation),
    "queued",
  );
});

void test("compaction status prioritizes manual running and queued work over automatic compaction", () => {
  const queued = { phase: "observing", operationId: firstExecutionId, queued: true } as const;
  assert.equal(compactionStatus(snapshot({ isCompacting: true }), queued), "queued");
  assert.equal(
    compactionStatus(
      snapshot({ isCompacting: true, queuedCompactionId: firstExecutionId }),
      undefined,
    ),
    "queued",
  );
  assert.equal(
    compactionStatus(
      snapshot({ isCompacting: true, manualCompactionId: firstExecutionId }),
      queued,
    ),
    "running",
  );
  assert.equal(compactionStatus(snapshot({ isCompacting: true }), undefined), null);
  assert.equal(compactionStatus(snapshot(), { ...queued, queued: false }), "running");
});

void test("acknowledged queued compaction allows steering but prevents duplicate compaction", async () => {
  const workspace = fixture();
  let starts = 0;
  let sends = 0;
  const coordinator = commands(workspace.access, {
    ...emptyClient(async (_id, submission) => {
      sends++;
      return receipt(submission);
    }),
    startCompaction: async (_id, submission) => {
      starts++;
      return { ...compactionReceipt(submission.operationId, null), queued: true };
    },
  });
  const active = snapshot({ unsettledExecutionId: firstExecutionId });
  await coordinator.compact(sessionId, active, true);
  await coordinator.compact(sessionId, active, true);
  await coordinator.send(sessionId, active, true, true);
  assert.equal(starts, 1);
  assert.equal(sends, 1);
});

void test("queued compaction is not reconciled until it leaves the server queue", async () => {
  const workspace = fixture();
  let reads = 0;
  const coordinator = commands(workspace.access, {
    ...emptyClient(),
    startCompaction: async (_id, submission) => ({
      ...compactionReceipt(submission.operationId, null),
      queued: true,
    }),
    getCompactionOperation: async () => {
      reads++;
      return null;
    },
  });
  await coordinator.compact(sessionId, snapshot({ unsettledExecutionId: firstExecutionId }), true);
  const operation = coordinator.status.compactions[sessionId];
  assert.equal(operation?.phase, "observing");
  if (operation?.phase !== "observing") return;

  coordinator.observeSnapshot(
    snapshot({
      unsettledExecutionId: firstExecutionId,
      queuedCompactionId: operation.operationId,
    }),
    null,
  );
  assert.equal(reads, 0);
  assert.equal(coordinator.status.compactions[sessionId]?.phase, "observing");
});

for (const previousOperation of [false, true]) {
  void test(`workspace tracks a server queue with ${previousOperation ? "a prior settled operation" : "no local operation"}`, async () => {
    const workspace = fixture();
    const settled = deferred<void>();
    const message = "Queued compaction skipped because history changed.";
    const coordinator = commands(
      workspace.access,
      {
        ...emptyClient(),
        startCompaction: async (_id, submission) => compactionReceipt(submission.operationId),
        getCompactionOperation: async (_id, operationId) => ({
          ...compactionReceipt(operationId, "cancelled"),
          error: message,
        }),
      },
      (state) => {
        const operation = state.compactions[sessionId];
        if (operation?.phase === "settled" && operation.message === message) settled.resolve();
      },
    );
    if (previousOperation) await coordinator.compact(sessionId, snapshot(), true);
    coordinator.observeSnapshot(snapshot({ queuedCompactionId: firstExecutionId }), null);
    assert.equal(coordinator.status.compactions[sessionId]?.phase, "observing");
    coordinator.observeSnapshot(snapshot(), null);
    await settled.promise;
    assert.deepEqual(coordinator.status.compactions[sessionId], { phase: "settled", message });
  });
}

void test("a later compaction receipt updates queued state", async () => {
  const workspace = fixture();
  const checked = deferred<void>();
  const coordinator = commands(
    workspace.access,
    {
      ...emptyClient(),
      startCompaction: async (_id, submission) => ({
        ...compactionReceipt(submission.operationId, null),
        queued: true,
      }),
      getCompactionOperation: async (_id, operationId) => compactionReceipt(operationId, null),
    },
    (state) => {
      const operation = state.compactions[sessionId];
      if (operation?.phase === "observing" && !operation.queued) checked.resolve();
    },
  );
  await coordinator.compact(sessionId, snapshot(), true);
  coordinator.observeSnapshot(snapshot(), null);
  await checked.promise;
  const operation = coordinator.status.compactions[sessionId];
  assert.equal(operation?.phase, "observing");
  if (operation?.phase === "observing") assert.equal(operation.queued, false);
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

void test("a missing compaction receipt resolves to unconfirmed without losing the draft", async () => {
  const workspace = fixture();
  const checked = deferred<void>();
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
      if (state.compactions[sessionId]?.phase === "settled") checked.resolve();
    },
  );

  await coordinator.compact(sessionId, snapshot(), true);
  const operation = coordinator.status.compactions[sessionId];
  assert.equal(operation?.phase, "uncertain");
  if (operation?.phase !== "uncertain") return;
  coordinator.observeSnapshot(snapshot(), null);
  await checked.promise;

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
      if (phase === "uncertain" && receiptReads.length > 0) uncertain.resolve();
      if (phase === "settled") settled.resolve();
    },
  );

  await coordinator.compact(sessionId, snapshot(), true);
  const operation = coordinator.status.compactions[sessionId];
  assert.equal(operation?.phase, "uncertain");
  if (operation?.phase !== "uncertain") return;
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
