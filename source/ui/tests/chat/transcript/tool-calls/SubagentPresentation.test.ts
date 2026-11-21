import assert from "node:assert/strict";
import test from "node:test";
import type {
  TranscriptEntryDescriptor,
  TranscriptEntryKind,
} from "../../../../sessions/transcript/Messages.ts";
import { subagentPresentation } from "../../../../chat/transcript/tool-calls/SubagentPresentation.ts";

function row(
  rank: number,
  kind: TranscriptEntryKind = "tool",
  options: {
    readonly name?: string | null;
    readonly requestId?: string;
    readonly callId?: string;
    readonly executionId?: string;
    readonly historyRevision?: number;
    readonly withTool?: boolean;
  } = {},
): { descriptor: TranscriptEntryDescriptor; rank: number } {
  const {
    name = "subagent",
    requestId = "request-a",
    callId = `call-${rank}`,
    executionId = "execution-a",
    historyRevision = 4,
    withTool = true,
  } = options;
  return {
    rank,
    descriptor: {
      entry: {
        executionId,
        entryId: `entry-${rank}`,
        kind,
        historyRevision,
      },
      createdAt: null,
      text: null,
      tool: withTool
        ? {
            requestId,
            callId,
            name,
            started: true,
            interrupted: false,
            background: false,
            batchedWithPrevious: false,
            batchedWithNext: false,
            outcome: null,
            isClosed: false,
            startedAt: null,
            endedAt: null,
          }
        : null,
    },
  };
}

void test("groups adjacent sibling calls and marks paired completions", () => {
  const result = subagentPresentation([
    row(20, "tool", { callId: "call-a" }),
    row(21, "tool", { callId: "call-b" }),
    row(22, "toolCompletion", { callId: "call-a" }),
  ]);

  assert.deepEqual([...result.keys()], [20, 21, 22]);
  assert.deepEqual(result.get(20), {
    kind: "call",
    heading: "Subagents",
    hasCompletion: true,
  });
  assert.deepEqual(result.get(21), {
    kind: "call",
    heading: null,
    hasCompletion: false,
  });
  assert.deepEqual(result.get(22), { kind: "hidden" });
  assert.equal(result.get(0), undefined);
});

void test("keeps unmatched completion rows as dedicated subagent rows", () => {
  const result = subagentPresentation([
    row(7, "toolCompletion", { callId: "same-call" }),
    row(8, "tool", { callId: "same-call", executionId: "other-execution" }),
    row(9, "tool", { callId: "same-call", historyRevision: 5 }),
    row(10, "tool", { callId: "same-call", requestId: "other-request" }),
    row(11, "tool", { callId: "other-call" }),
  ]);

  assert.deepEqual(result.get(7), {
    kind: "completion",
    heading: "Subagent",
    hasCompletion: true,
  });
  assert.deepEqual(result.get(8), { kind: "call", heading: "Subagent", hasCompletion: false });
  assert.deepEqual(result.get(9), { kind: "call", heading: "Subagent", hasCompletion: false });
  assert.deepEqual(result.get(10), { kind: "call", heading: "Subagent", hasCompletion: false });
  assert.deepEqual(result.get(11), { kind: "call", heading: "Subagent", hasCompletion: false });
});

void test("restarts grouping across rank gaps, intervening entries, or different call scopes", () => {
  const result = subagentPresentation([
    row(1, "tool", { callId: "one" }),
    row(2, "text"),
    row(3, "tool", { callId: "two" }),
    row(4, "tool", { callId: "three", requestId: "request-b" }),
    row(5, "tool", { callId: "four", executionId: "execution-b" }),
    row(6, "tool", { callId: "five", historyRevision: 5 }),
    row(8, "tool", { callId: "six" }),
  ]);

  for (const rank of [1, 3, 4, 5, 6, 8]) {
    assert.deepEqual(result.get(rank), {
      kind: "call",
      heading: "Subagent",
      hasCompletion: false,
    });
  }
  assert.equal(result.get(2), undefined);
});

void test("only exact normalized subagent tool and transcript tool kinds are presented", () => {
  const result = subagentPresentation([
    row(30, "tool", { name: "Subagent" }),
    row(31, "tool", { name: "subagent_worker" }),
    row(32, "tool", { withTool: false }),
    row(33, "text", { name: "subagent" }),
    row(34, "toolCompletion", { name: "subagent" }),
  ]);

  for (const rank of [30, 31, 32, 33]) assert.equal(result.get(rank), undefined);
  assert.deepEqual(result.get(34), {
    kind: "completion",
    heading: "Subagent",
    hasCompletion: true,
  });
});

void test("pairs completion rows even when they precede calls in the loaded rows", () => {
  const result = subagentPresentation([
    row(50, "toolCompletion", { callId: "paired" }),
    row(51, "tool", { callId: "paired" }),
  ]);

  assert.deepEqual(result.get(50), { kind: "hidden" });
  assert.deepEqual(result.get(51), { kind: "call", heading: "Subagent", hasCompletion: true });
});
