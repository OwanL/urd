import assert from "node:assert/strict";
import test from "node:test";
import type { ToolCallLabel, TranscriptToolFacts } from "../sessions/Messages.ts";
import {
  formatElapsedTime,
  isToolCallRunning,
  toolCallLabel,
  toolElapsedMilliseconds,
  toolRowTimeLabel,
} from "../app/ToolRowPresentation.ts";

const tool: TranscriptToolFacts = {
  requestId: "request-a",
  callId: "call-b",
  name: "read_file",
  started: true,
  background: false,
  batchedWithPrevious: false,
  batchedWithNext: false,
  outcome: null,
  isClosed: false,
  startedAt: "1970-01-01T00:00:00.000Z",
  endedAt: null,
};
const label: ToolCallLabel = {
  icon: "read",
  title: "Messages.ts",
  detail: "from line 20",
  path: "C:/source/Messages.ts",
};

void test("formats elapsed time at the five-second threshold in whole units", () => {
  assert.equal(formatElapsedTime(4_999), null);
  assert.equal(formatElapsedTime(5_999), "5s");
  assert.equal(formatElapsedTime(133_999), "2m 13s");
  assert.equal(formatElapsedTime(Number.NaN), null);
});

void test("runs live elapsed time until an end timestamp freezes it", () => {
  assert.equal(isToolCallRunning(tool, false), true);
  assert.equal(toolElapsedMilliseconds(tool, 7_000, false), 7_000);

  const ended = { ...tool, endedAt: "1970-01-01T00:00:06.000Z" };
  assert.equal(isToolCallRunning(ended, false), false);
  assert.equal(toolElapsedMilliseconds(ended, 10_000, false), 6_000);
});

void test("keeps background status accessible with and without elapsed time", () => {
  assert.equal(toolRowTimeLabel({ background: true }, null, false), "Background call");
  assert.equal(toolRowTimeLabel({ background: true }, "7s", false), "Background call, elapsed 7s");
  assert.equal(toolRowTimeLabel({ background: true }, "2m 13s", true), null);
  assert.equal(toolRowTimeLabel({ background: false }, "7s", false), null);
});

void test("looks up call labels by request and call id", () => {
  assert.equal(toolCallLabel(tool, { "request-a/call-b": label }), label);
  assert.equal(toolCallLabel(tool, {}), null);
});
