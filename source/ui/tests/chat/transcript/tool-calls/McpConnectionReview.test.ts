import { strict as assert } from "node:assert";
import { test } from "node:test";
import {
  canReviewMcpConnection,
  proposedMcpUrl,
} from "../../../../chat/transcript/tool-calls/McpConnectionReview.ts";
import type { TranscriptToolFacts } from "../../../../sessions/transcript/Messages.ts";

void test("review requires a successfully completed proposal call", () => {
  const tool: TranscriptToolFacts = {
    name: "propose_mcp_connection",
    requestId: "r",
    callId: "c",
    started: true,
    interrupted: false,
    background: false,
    batchedWithPrevious: false,
    batchedWithNext: false,
    outcome: "completed",
    isClosed: true,
    startedAt: null,
    endedAt: null,
  };
  assert.equal(canReviewMcpConnection(tool, false), true);
  assert.equal(canReviewMcpConnection(tool, true), false);
  for (const change of [
    { outcome: "failed" as const },
    { outcome: null },
    { started: false },
    { interrupted: true },
    { isClosed: false },
    { name: "other" },
  ])
    assert.equal(canReviewMcpConnection({ ...tool, ...change }, false), false);
});

const input = (url: string) => JSON.stringify({ name: "Server", url });

void test("accepts HTTPS and loopback HTTP proposals", () => {
  for (const url of [
    "https://example.com/mcp",
    "http://localhost:123/mcp",
    "http://127.0.0.2/mcp",
    "http://[::1]/mcp",
  ])
    assert.equal(proposedMcpUrl(input(url)), url);
});

void test("rejects unsafe or malformed proposal URLs", () => {
  for (const url of [
    "http://example.com",
    "ftp://localhost",
    "https://user@example.com",
    "https://@example.com",
    "https://example.com/?",
    "https://example.com/#",
    "https://example.com/?token=x",
    "https://example.com/#x",
    " https://example.com",
    "not a url",
  ])
    assert.equal(proposedMcpUrl(input(url)), null, url);
  for (const value of ["not json", "null", "[]", "{}", '{"url":5}'])
    assert.equal(proposedMcpUrl(value), null, value);
});
