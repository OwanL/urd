import assert from "node:assert/strict";
import test from "node:test";
import { RequestError } from "../http/RequestError.ts";
import { SessionClient } from "../sessions/SessionClient.ts";
import type { LlmSelection } from "../providers/LlmSelection.ts";
import type { MessageContentCursor, MessageContentPage } from "../sessions/Messages.ts";
import type { SessionSnapshot } from "../sessions/SessionSnapshot.ts";
import type {
  UserMessageReceipt,
  UserMessageSubmission,
} from "../sessions/UserMessageSubmission.ts";
import { startHttpServer } from "./HttpTestServer.ts";

const sessionId = "10000000-0000-0000-0000-000000000001";
const executionId = "20000000-0000-0000-0000-000000000002";
const submissionId = "30000000-0000-0000-0000-000000000003";
const cursor: MessageContentCursor = {
  message: {
    executionId,
    messageId: "40000000-0000-0000-0000-000000000004",
    role: "assistant",
    historyRevision: 7,
  },
  segmentIndex: 3,
  utf8Offset: 11,
};
const submission: UserMessageSubmission = {
  submissionId,
  expectedHistoryRevision: 7,
  text: "  unchanged\r\nUnicode: 雪 🦉\t  ",
  llmSelection: null,
};
const receipt: UserMessageReceipt = {
  sessionId,
  submission,
  acceptance: "accepted",
  refusalReason: null,
  executionId,
};
const snapshot: SessionSnapshot = {
  sessionId,
  historyRevision: 7,
  admissionFenced: false,
  unsettledExecutionId: executionId,
  stopRequested: false,
  lastSettlement: null,
  latestLlmSelection: null,
  messages: [],
  hasOlderMessages: true,
};
const selection: LlmSelection = {
  connection: {
    id: "50000000-0000-0000-0000-000000000005",
    name: "Saved connection 雪",
    route: "chatGpt",
    api: "openAIResponses",
    endpoint: "https://provider.example/v1/",
    authentication: "oAuth",
  },
  modelId: "provider/model-id",
  reasoningEffort: null,
};
const selectedSubmission: UserMessageSubmission = { ...submission, llmSelection: selection };
const selectedReceipt: UserMessageReceipt = { ...receipt, submission: selectedSubmission };
const content: MessageContentPage = {
  from: cursor,
  text: "streamed text 雪",
  next: { ...cursor, utf8Offset: 28 },
  availableThrough: { ...cursor, utf8Offset: 28 },
  hasMore: false,
  isClosed: false,
  finalOutputCommitted: false,
};

interface MappingCase {
  name: string;
  call: (client: SessionClient) => Promise<unknown>;
  method: "GET" | "POST";
  path: string;
  query?: Record<string, string>;
  body?: unknown;
  response: unknown;
  expected?: unknown;
  status?: number;
}

const opaqueCursor = "opaque /+?&=#%雪";
const page = { sessionIds: [sessionId], nextCursor: opaqueCursor };
const stopReceipt = { executionId, status: "requested", outcome: null };
const selectedRefusal: UserMessageReceipt = {
  ...selectedReceipt,
  acceptance: "refused",
  refusalReason: "selectionLocked",
  executionId: null,
};
const activeSnapshot: SessionSnapshot = { ...snapshot, latestLlmSelection: selection };
const settledSnapshot: SessionSnapshot = {
  ...snapshot,
  unsettledExecutionId: null,
  lastSettlement: { executionId, outcome: "completed" },
  latestLlmSelection: selection,
};
const mappings: MappingCase[] = [
  {
    name: "list without a cursor",
    call: (client) => client.list(),
    method: "GET",
    path: "/api/sessions",
    response: page,
  },
  {
    name: "list preserves an opaque cursor",
    call: (client) => client.list(opaqueCursor),
    method: "GET",
    path: "/api/sessions",
    query: { cursor: opaqueCursor },
    response: page,
  },
  {
    name: "list preserves a supplied empty cursor",
    call: (client) => client.list(""),
    method: "GET",
    path: "/api/sessions",
    query: { cursor: "" },
    response: page,
  },
  {
    name: "create sends an empty JSON object and returns the ID",
    call: (client) => client.create(),
    method: "POST",
    path: "/api/sessions",
    body: {},
    response: { sessionId },
    expected: sessionId,
    status: 201,
  },
  {
    name: "snapshot",
    call: (client) => client.getSnapshot(sessionId),
    method: "GET",
    path: `/api/sessions/${sessionId}/snapshot`,
    response: snapshot,
  },
  {
    name: "content flattens every cursor member",
    call: (client) => client.getMessageContent(sessionId, cursor),
    method: "GET",
    path: `/api/sessions/${sessionId}/content`,
    query: {
      executionId,
      messageId: cursor.message.messageId,
      role: "assistant",
      historyRevision: "7",
      segmentIndex: "3",
      utf8Offset: "11",
    },
    response: content,
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
    name: "submit roundtrips the full initiating selection",
    call: (client) => client.submitMessage(sessionId, selectedSubmission),
    method: "POST",
    path: `/api/sessions/${sessionId}/message-submissions`,
    body: selectedSubmission,
    response: selectedReceipt,
  },
  {
    name: "selection-locked refusal preserves the submitted selection",
    call: (client) => client.getMessageReceipt(sessionId, submissionId),
    method: "GET",
    path: `/api/sessions/${sessionId}/message-submissions/${submissionId}`,
    response: selectedRefusal,
  },
  {
    name: "active snapshot preserves the latest initiating selection",
    call: (client) => client.getSnapshot(sessionId),
    method: "GET",
    path: `/api/sessions/${sessionId}/snapshot`,
    response: activeSnapshot,
  },
  {
    name: "settled snapshot retains the latest initiating selection",
    call: (client) => client.getSnapshot(sessionId),
    method: "GET",
    path: `/api/sessions/${sessionId}/snapshot`,
    response: settledSnapshot,
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
];

for (const mapping of mappings) {
  void test(mapping.name, { timeout: 5_000 }, async (t) => {
    const server = await startHttpServer((_request, response) => {
      response.writeHead(mapping.status ?? 200, { "Content-Type": "application/json" });
      response.end(JSON.stringify(mapping.response));
    });
    t.after(() => server.close());

    const baseUrl = new URL("/ignored/path?ignored=query", server.origin);
    const client = new SessionClient(baseUrl);
    const expected = mapping.expected === undefined ? mapping.response : mapping.expected;
    assert.deepEqual(await mapping.call(client), expected);
    assert.equal(server.requests.length, 1);
    const request = server.requests[0];
    assert.ok(request);
    assert.equal(request.method, mapping.method);
    assert.equal(request.headers["x-urd-client"], "browser");
    assert.equal(request.headers.accept, "application/json");
    assert.equal(
      request.headers["content-type"],
      mapping.method === "POST" ? "application/json" : undefined,
    );
    const url = new URL(request.url, server.origin);
    assert.equal(url.pathname, mapping.path);
    assert.deepEqual(
      [...url.searchParams.entries()].sort(),
      Object.entries(mapping.query ?? {}).sort(),
    );
    assert.equal(request.body, mapping.body === undefined ? "" : JSON.stringify(mapping.body));
  });
}

void test("construction does no I/O", (t) => {
  const fetchMock = t.mock.method(globalThis, "fetch", async () => new Response("null"));
  new SessionClient(new URL("https://server.example"));
  assert.equal(fetchMock.mock.callCount(), 0);
});

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

void test("create rejects invalid session identities", { timeout: 5_000 }, async (t) => {
  const invalid = [
    {},
    { sessionId: null },
    { sessionId: 42 },
    { sessionId: "" },
    { sessionId: "not-a-session-id" },
    { sessionId: "00000000-0000-0000-0000-000000000000" },
  ];
  const responses: unknown[] = [...invalid];
  const server = await startHttpServer((_request, response) => {
    response.writeHead(201, { "Content-Type": "application/json" });
    response.end(JSON.stringify(responses.shift()));
  });
  t.after(() => server.close());
  const client = new SessionClient(server.origin);

  for (const response of invalid)
    await assert.rejects(client.create(), TypeError, JSON.stringify(response));
  assert.equal(server.requests.length, invalid.length);
});

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

const commands: { name: string; call: (client: SessionClient) => Promise<unknown> }[] = [
  { name: "create", call: (client) => client.create() },
  { name: "submit", call: (client) => client.submitMessage(sessionId, submission) },
  { name: "Stop", call: (client) => client.stopExecution(sessionId, executionId) },
];
for (const command of commands) {
  for (const phase of ["request", "acknowledgement"] as const) {
    void test(
      `${command.name} never retries after ${phase} disconnect`,
      { timeout: 5_000 },
      async (t) => {
        const server = await startHttpServer((_request, response) => {
          if (phase === "request") {
            response.destroy();
            return;
          }
          response.writeHead(200, { "Content-Type": "application/json", "Content-Length": "1000" });
          response.flushHeaders();
          response.write('{"partial":', () => response.destroy());
        });
        t.after(() => server.close());

        await assert.rejects(command.call(new SessionClient(server.origin)));
        assert.equal(server.requests.length, 1);
        assert.equal(server.requests[0]?.method, "POST");
      },
    );
  }
}

const reads: {
  name: string;
  call: (client: SessionClient, signal: AbortSignal) => Promise<unknown>;
}[] = [
  { name: "list", call: (client, signal) => client.list(undefined, signal) },
  { name: "snapshot", call: (client, signal) => client.getSnapshot(sessionId, signal) },
  {
    name: "content",
    call: (client, signal) => client.getMessageContent(sessionId, cursor, signal),
  },
  {
    name: "receipt",
    call: (client, signal) => client.getMessageReceipt(sessionId, submissionId, signal),
  },
];
for (const read of reads) {
  void test(
    `cancelling ${read.name} remains a cancelled read and never sends Stop`,
    { timeout: 5_000 },
    async (t) => {
      let received!: () => void;
      const requestReceived = new Promise<void>((resolve) => {
        received = resolve;
      });
      const server = await startHttpServer(() => received());
      t.after(() => server.close());
      const controller = new AbortController();
      t.after(() => controller.abort());
      const pending = read.call(new SessionClient(server.origin), controller.signal);
      const rejected = assert.rejects(pending, { name: "AbortError" });
      await requestReceived;
      controller.abort();
      await rejected;

      assert.equal(server.requests.length, 1);
      assert.equal(server.requests[0]?.method, "GET");
    },
  );
}
