import assert from "node:assert/strict";
import test from "node:test";
import type { StopReceipt } from "../../sessions/inputs/Stop.ts";
import {
  sessionId,
  firstExecutionId,
  nextExecutionId,
  snapshot,
  fixture,
  commands,
  emptyClient,
  deferred,
} from "./WorkspaceCommandsFixture.ts";

void test("a late Stop failure for an earlier execution cannot replace or block Stop for its successor", async () => {
  const workspace = fixture();
  const first = deferred<StopReceipt>();
  const second = deferred<StopReceipt>();
  const third = deferred<StopReceipt>();
  const executions: string[] = [];
  const coordinator = commands(
    workspace.access,
    emptyClient(undefined, async (_id, executionId) => {
      executions.push(executionId);
      if (executions.length === 1) return first.promise;
      if (executions.length === 2) return second.promise;
      return third.promise;
    }),
  );

  const stopFirst = coordinator.stop(
    sessionId,
    snapshot({ unsettledExecutionId: firstExecutionId }),
    undefined,
  );
  coordinator.observeSnapshot(
    snapshot({
      unsettledExecutionId: nextExecutionId,
      lastSettlement: { executionId: firstExecutionId, outcome: "completed" },
    }),
    null,
  );
  const stopSecond = coordinator.stop(
    sessionId,
    snapshot({ unsettledExecutionId: nextExecutionId }),
    undefined,
  );
  second.reject(new Error("acknowledgement lost"));
  await stopSecond;
  assert.equal(coordinator.status.stopping[sessionId]?.executionId, nextExecutionId);
  assert.equal(coordinator.status.stopping[sessionId]?.pending, false);

  first.reject(new Error("older acknowledgement lost"));
  await stopFirst;
  assert.equal(coordinator.status.stopping[sessionId]?.executionId, nextExecutionId);
  assert.match(coordinator.status.stopping[sessionId]?.error ?? "", /acknowledgement lost/);

  const retrySecond = coordinator.stop(
    sessionId,
    snapshot({ unsettledExecutionId: nextExecutionId }),
    undefined,
  );
  assert.deepEqual(executions, [firstExecutionId, nextExecutionId, nextExecutionId]);
  third.resolve({ executionId: nextExecutionId, status: "requested", outcome: null });
  await retrySecond;
});
