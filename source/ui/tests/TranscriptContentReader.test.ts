import assert from "node:assert/strict";
import test from "node:test";
import { TranscriptContentReader } from "../app/TranscriptContentReader.ts";

function deferred() {
  let resolve!: () => void;
  const promise = new Promise<void>((done) => {
    resolve = done;
  });
  return { promise, resolve };
}

void test("serializes session content reads and skips queued work after abort", async () => {
  const reader = new TranscriptContentReader();
  const firstGate = deferred();
  const firstController = new AbortController();
  const queuedController = new AbortController();
  const lastController = new AbortController();
  let active = 0;
  let maximumActive = 0;
  let queuedDispatches = 0;

  async function read(gate?: ReturnType<typeof deferred>) {
    active++;
    maximumActive = Math.max(maximumActive, active);
    try {
      if (gate) await gate.promise;
    } finally {
      active--;
    }
  }

  const first = reader.read("session", firstController.signal, () => read(firstGate));
  const aborted = reader.read("session", queuedController.signal, async () => {
    queuedDispatches++;
    await read();
  });
  const last = reader.read("session", lastController.signal, () => read());

  queuedController.abort();
  await assert.rejects(aborted);
  assert.equal(queuedDispatches, 0);
  assert.equal(active, 1);

  firstGate.resolve();
  await Promise.all([first, last]);

  assert.equal(queuedDispatches, 0);
  assert.equal(maximumActive, 1);
  assert.equal(active, 0);
});
