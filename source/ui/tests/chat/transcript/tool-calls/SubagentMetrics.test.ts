import assert from "node:assert/strict";
import test from "node:test";
import {
  shortSubagentModel,
  subagentCostLabel,
  subagentElapsed,
} from "../../../../chat/transcript/tool-calls/SubagentMetrics.ts";
import type { SubagentCall } from "../../../../sessions/SubagentCall.ts";
import type { TranscriptToolFacts } from "../../../../sessions/transcript/Messages.ts";

const missing = { usd: null, partial: false };
const tool: TranscriptToolFacts = {
  requestId: "request",
  callId: "call",
  name: "subagent",
  started: true,
  interrupted: false,
  background: true,
  batchedWithPrevious: false,
  batchedWithNext: false,
  outcome: null,
  isClosed: false,
  startedAt: "2026-11-28T09:58:00Z",
  endedAt: null,
};
const live: SubagentCall = {
  id: "child",
  parentId: "parent",
  title: "Child",
  titlePending: false,
  live: true,
  modelId: "claude-sonnet-4-6",
  startedAt: "2026-11-28T10:00:00Z",
  elapsedMilliseconds: null,
  durationIncomplete: false,
  cost: { own: missing, descendants: missing, total: missing, hasDescendants: false },
};

void test("subagent elapsed time uses the child's start and freezes at its measured duration", () => {
  const now = Date.parse("2026-11-28T10:00:24Z");
  assert.equal(subagentElapsed(live, tool, now).text, "0:24");
  const settled = { ...live, startedAt: null, elapsedMilliseconds: 78_000, live: false };
  assert.deepEqual(subagentElapsed(settled, tool, now + 100_000), {
    text: "1:18",
    description: "Elapsed time: 1:18",
    running: false,
  });
  assert.equal(subagentElapsed(null, { ...tool, started: false }, now).text, "Queued");
  assert.equal(subagentElapsed(null, { ...tool, interrupted: true }, now).text, "Interrupted");
  assert.equal(subagentElapsed({ ...settled, durationIncomplete: true }, tool, now).text, "≥1:18");
});

void test("subagent cost distinguishes missing, zero and partial pricing", () => {
  assert.equal(subagentCostLabel(missing), "—");
  assert.equal(subagentCostLabel({ usd: null, partial: true }), "—");
  assert.equal(subagentCostLabel({ usd: 0, partial: false }), "$0.00");
  assert.equal(subagentCostLabel({ usd: 0.004, partial: false }), "<$0.01");
  assert.equal(subagentCostLabel({ usd: 0.084, partial: true }), "~$0.08");
});

void test("narrow rows shorten known display prefixes without renaming other models", () => {
  assert.equal(shortSubagentModel("Claude Sonnet 4.6"), "Sonnet 4.6");
  assert.equal(shortSubagentModel("claude-haiku-4-5"), "haiku-4-5");
  assert.equal(shortSubagentModel("GPT-5.4 Mini"), "GPT-5.4 Mini");
});
