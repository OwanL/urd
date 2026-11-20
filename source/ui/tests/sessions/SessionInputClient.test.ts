import assert from "node:assert/strict";
import test from "node:test";
import { SessionClient } from "../../sessions/SessionClient.ts";
import { RequestError } from "../../http/RequestError.ts";
import type { UserMessageReceipt } from "../../sessions/inputs/UserMessageSubmission.ts";
import { startHttpServer } from "../support/HttpTestServer.ts";
import { testSessionMappings, type MappingCase } from "./SessionClientMapping.ts";
import {
  sessionId,
  executionId,
  submissionId,
  operationId,
  compactionId,
  submission,
  receipt,
  continuationSubmission,
  continuationReceipt,
  stopReceipt,
  compactionSubmission,
  compactionReceipt,
  compactionSummary,
} from "./SessionClientFixtures.ts";

const mappings: MappingCase[] = [
  {
    name: "manual Continue preserves its exact revision, prior execution and selection",
    call: (client) => client.continueSession(sessionId, continuationSubmission),
    method: "POST",
    path: `/api/sessions/${sessionId}/continuations`,
    body: continuationSubmission,
    response: continuationReceipt,
  },
  {
    name: "manual Continue receipt lookup uses the stable operation ID",
    call: (client) => client.getContinuationReceipt(sessionId, operationId),
    method: "GET",
    path: `/api/sessions/${sessionId}/continuations/${operationId}`,
    response: continuationReceipt,
  },
  {
    name: "pending-input removal targets its execution and submission",
    call: (client) => client.removePendingInput(sessionId, executionId, submissionId),
    method: "POST",
    path: `/api/sessions/${sessionId}/executions/${executionId}/pending-inputs/${submissionId}/remove`,
    body: {},
    response: true,
    expected: true,
  },
  {
    name: "submit preserves the original submission exactly",
    call: (client) => client.submitMessage(sessionId, submission),
    method: "POST",
    path: `/api/sessions/${sessionId}/message-submissions`,
    body: submission,
    response: receipt,
  },
  {
    name: "receipt",
    call: (client) => client.getMessageReceipt(sessionId, submissionId),
    method: "GET",
    path: `/api/sessions/${sessionId}/message-submissions/${submissionId}`,
    response: receipt,
  },
  {
    name: "Stop sends an empty JSON object",
    call: (client) => client.stopExecution(sessionId, executionId),
    method: "POST",
    path: `/api/sessions/${sessionId}/executions/${executionId}/stop`,
    body: {},
    response: stopReceipt,
  },
  {
    name: "manual compaction preserves the frozen selection and focus",
    call: (client) => client.startCompaction(sessionId, compactionSubmission),
    method: "POST",
    path: `/api/sessions/${sessionId}/compactions`,
    body: compactionSubmission,
    response: compactionReceipt,
  },
  {
    name: "compaction operation receipt lookup",
    call: (client) => client.getCompactionOperation(sessionId, operationId),
    method: "GET",
    path: `/api/sessions/${sessionId}/compactions/operations/${operationId}`,
    response: compactionReceipt,
  },
  {
    name: "compaction summary is fenced by history revision",
    call: (client) => client.getCompactionSummary(sessionId, compactionId, 8),
    method: "GET",
    path: `/api/sessions/${sessionId}/compactions/${compactionId}`,
    query: { expectedHistoryRevision: "8" },
    response: compactionSummary,
  },
];

testSessionMappings(mappings);

void test("a missing receipt is null, not a refused submission", { timeout: 5_000 }, async (t) => {
  const refused: UserMessageReceipt = {
    ...receipt,
    acceptance: "refused",
    refusalReason: "staleRevision",
    executionId: null,
  };
  const responses: unknown[] = [null, refused];
  const server = await startHttpServer((_request, response) => {
    response.writeHead(200, { "Content-Type": "application/json" });
    response.end(JSON.stringify(responses.shift()));
  });
  t.after(() => server.close());
  const client = new SessionClient(server.origin);

  assert.equal(await client.getMessageReceipt(sessionId, submissionId), null);
  assert.deepEqual(await client.getMessageReceipt(sessionId, submissionId), refused);
  assert.equal(server.requests.length, 2);
});

void test(
  "pending input reads the original receipt text on demand",
  { timeout: 5_000 },
  async (t) => {
    const text = "long input 🦉 ".repeat(200);
    const responses: unknown[] = [{ ...receipt, submission: { ...submission, text } }, null];
    const server = await startHttpServer((_request, response) => {
      response.writeHead(200, { "Content-Type": "application/json" });
      response.end(JSON.stringify(responses.shift()));
    });
    t.after(() => server.close());
    const client = new SessionClient(server.origin);
    assert.equal(await client.getPendingInputText(sessionId, submissionId), text);
    await assert.rejects(client.getPendingInputText(sessionId, submissionId), /unavailable/);
    assert.equal(
      server.requests[0]?.url,
      `/api/sessions/${sessionId}/message-submissions/${submissionId}`,
    );
  },
);

void test(
  "unavailable Stop remains an HTTP conflict with its original receipt",
  { timeout: 5_000 },
  async (t) => {
    const unavailable = { executionId, status: "unavailable", outcome: null };
    const server = await startHttpServer((_request, response) => {
      response.writeHead(409, { "Content-Type": "application/json" });
      response.end(JSON.stringify(unavailable));
    });
    t.after(() => server.close());

    await assert.rejects(
      new SessionClient(server.origin).stopExecution(sessionId, executionId),
      (error) => {
        assert.ok(error instanceof RequestError);
        assert.equal(error.status, 409);
        assert.deepEqual(error.responseBody, unavailable);
        return true;
      },
    );
    assert.equal(server.requests.length, 1);
    assert.equal(server.requests[0]?.body, "{}");
  },
);

void test("a disconnected submission is not retried", { timeout: 5_000 }, async (t) => {
  const server = await startHttpServer((_request, response) => response.destroy());
  t.after(() => server.close());

  await assert.rejects(new SessionClient(server.origin).submitMessage(sessionId, submission));
  assert.equal(server.requests.length, 1);
  assert.equal(server.requests[0]?.method, "POST");
});
