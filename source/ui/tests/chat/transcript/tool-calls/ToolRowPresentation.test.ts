import assert from "node:assert/strict";
import test from "node:test";
import type {
  ToolCallLabel,
  TranscriptToolFacts,
} from "../../../../sessions/transcript/Messages.ts";
import {
  formatElapsedTime,
  isToolCallFailed,
  toolCallLabel,
  toolElapsedMilliseconds,
  toolRowStatus,
} from "../../../../chat/transcript/tool-calls/ToolRowPresentation.ts";

const tool: TranscriptToolFacts = {
  requestId: "request-a",
  callId: "call-b",
  name: "read_file",
  started: true,
  interrupted: false,
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
  assert.equal(formatElapsedTime(3_723_000), "1h 2m");
  assert.equal(formatElapsedTime(86_400_000), "1d");
  assert.equal(formatElapsedTime(93_600_000), "1d 2h");
  assert.equal(formatElapsedTime(Number.NaN), null);
});

void test("marks only started, non-interrupted tool errors as failures", () => {
  assert.equal(isToolCallFailed({ outcome: "failed", started: true, interrupted: false }), true);
  assert.equal(isToolCallFailed({ outcome: "failed", started: false, interrupted: false }), false);
  assert.equal(isToolCallFailed({ outcome: "failed", started: true, interrupted: true }), false);
  assert.equal(
    isToolCallFailed({ outcome: "completed", started: true, interrupted: false }),
    false,
  );
});

void test("classifies tool lifecycle with interruption and outcome precedence", () => {
  assert.equal(toolRowStatus(tool, false), "running");
  assert.equal(toolRowStatus({ ...tool, started: false }, false), "pending");
  assert.equal(
    toolRowStatus({ ...tool, interrupted: true, outcome: "completed" }, false),
    "interrupted",
  );
  assert.equal(
    toolRowStatus({ ...tool, started: false, interrupted: true, outcome: "failed" }, false),
    "not-run",
  );
  assert.equal(toolRowStatus({ ...tool, outcome: "completed" }, false), "completed");
  assert.equal(toolRowStatus({ ...tool, outcome: "failed" }, false), "failed");
  assert.equal(toolRowStatus({ ...tool, outcome: "cancelled" }, false), "cancelled");
  assert.equal(toolRowStatus({ ...tool, started: false, isClosed: true }, false), "not-run");
  assert.equal(toolRowStatus({ ...tool, started: false, isClosed: true }, true), "unknown");
  assert.equal(toolRowStatus({ ...tool, isClosed: true }, false), "unknown");
  assert.equal(toolRowStatus({ ...tool, endedAt: "1970-01-01T00:00:06.000Z" }, false), "unknown");
  assert.equal(toolRowStatus(tool, true), "unknown");
});

void test("elapsed time follows lifecycle and freezes at the end timestamp", () => {
  assert.equal(toolElapsedMilliseconds(tool, 7_000, false), 7_000);

  const ended = { ...tool, endedAt: "1970-01-01T00:00:06.000Z" };
  assert.equal(toolElapsedMilliseconds(ended, 10_000, false), 6_000);
  assert.equal(
    toolElapsedMilliseconds({ ...tool, interrupted: true, endedAt: ended.endedAt }, 10_000, false),
    6_000,
  );
  assert.equal(toolElapsedMilliseconds({ ...tool, interrupted: true }, 10_000, false), null);
  assert.equal(toolElapsedMilliseconds({ ...tool, isClosed: true }, 10_000, false), null);
  assert.equal(toolElapsedMilliseconds(ended, 10_000, true), 6_000);
  assert.equal(toolElapsedMilliseconds(tool, 10_000, true), null);
});

void test("looks up call labels by request and call id", () => {
  assert.equal(toolCallLabel(tool, { "request-a/call-b": label }), label);
  assert.equal(toolCallLabel(tool, {}), null);
});
