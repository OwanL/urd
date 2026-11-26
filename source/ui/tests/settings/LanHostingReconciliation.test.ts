import assert from "node:assert/strict";
import test from "node:test";
import {
  lostAcknowledgement,
  observeRead,
  readApplies,
} from "../../settings/LanHostingReconciliation.ts";

const status = { enabled: true, remembered: false, origins: ["on"], startupError: null };

void test("a read held by an in-flight command or a newer acknowledgement is not applied", () => {
  assert.equal(readApplies(1, 1, false), true);
  assert.equal(readApplies(1, 1, true), false);
  assert.equal(readApplies(1, 2, false), false);
});

void test("a lost POST acknowledgement shows the unknown state and holds its error across reads", () => {
  const afterLoss = lostAcknowledgement("Request failed (HTTP 503).");
  assert.equal(afterLoss.status, null);
  assert.equal(afterLoss.error, "Request failed (HTTP 503).");

  // A later reconciling read proves the current state, not that the command had no effect.
  const reconciled = observeRead({ status, error: null }, afterLoss.error);
  assert.equal(reconciled.status, status);
  assert.equal(reconciled.error, "Request failed (HTTP 503).");

  // A read after the next command started may clear the held error.
  const afterNewCommand = observeRead({ status, error: null }, null);
  assert.equal(afterNewCommand.error, null);
});
