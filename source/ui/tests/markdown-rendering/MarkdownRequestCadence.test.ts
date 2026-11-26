import assert from "node:assert/strict";
import test from "node:test";
import { MarkdownRequestCadence } from "../../chat/transcript/MarkdownRequestCadence.ts";

void test("streaming requests are coalesced on a fixed cadence and closed output bypasses it", () => {
  const cadence = new MarkdownRequestCadence();
  assert.equal(cadence.delay(0, false), 0, "the first extent is immediate");
  cadence.started(0);
  assert.equal(cadence.delay(20, false), 80);
  assert.equal(cadence.delay(70, false), 30, "later updates do not push out the deadline");
  assert.equal(cadence.delay(70, true), 0, "closed output is immediate");
  assert.equal(cadence.delay(100, false), 0, "the latest extent is due");
  cadence.started(100);
  assert.equal(cadence.delay(120, false), 80);
  cadence.reset();
  assert.equal(cadence.delay(120, false), 0, "retry or remount is immediate");
});
