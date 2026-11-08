import assert from "node:assert/strict";
import test from "node:test";
import { SessionClient } from "../sessions/SessionClient.ts";
import { RequestError } from "../http/RequestError.ts";
import type { LlmSelection } from "../providers/LlmSelection.ts";
import type { MessageContentCursor, MessageContentPage } from "../sessions/Messages.ts";
import type { SessionSnapshot } from "../sessions/SessionSnapshot.ts";
import type {
  UserMessageSubmission,
  UserMessageReceipt,
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

type Operation = (client: SessionClient) => Promise<unknown>;
interface MappingCase {
  name: string;
  call: Operation;
  method: string;
  path: string;
  query?: Record<string, string>;
  body?: unknown;
  response: unknown;
  expected: unknown;
}

const opaqueCursor = "opaque /+?&=#%雪";
const page = { sessionIds: [sessionId], nextCursor: opaqueCursor };
const stop = { executionId, status: "requested", outcome: null };
const mappings: MappingCase[] = [
  {
    name: "list without a cursor",
    call: (client) => client.list(),
    method: "GET",
    path: "/api/sessions",
    response: page,
    expected: page,
  },
  {
    name: "list preserves an opaque cursor",
    call: (client) => client.list(opaqueCursor),
    method: "GET",
    path: "/api/sessions",
    query: { cursor: opaqueCursor },
    response: page,
    expected: page,
  },
  {
    name: "list preserves a supplied empty cursor",
    call: (client) => client.list(""),
    method: "GET",
    path: "/api/sessions",
    query: { cursor: "" },
    response: page,
    expected: page,
  },
  {
    name: "create sends an empty JSON object and returns the ID",
    call: (client) => client.create(),
    method: "POST",
    path: "/api/sessions",
    body: {},
    response: { sessionId },
    expected: sessionId,
  },
  {
    name: "snapshot",
    call: (client) => client.getSnapshot(sessionId),
    method: "GET",
    path: `/api/sessions/${sessionId}/snapshot`,
    response: snapshot,
    expected: snapshot,
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
    expected: content,
  },
  {
    name: "submit preserves the original submission exactly",
    call: (client) => client.submitMessage(sessionId, submission),
    method: "POST",
    path: `/api/sessions/${sessionId}/message-submissions`,
    body: submission,
    response: receipt,
    expected: receipt,
  },
  {
    name: "submit roundtrips the full initiating selection without reducing its connection to an ID",
    call: (client) => client.submitMessage(sessionId, selectedSubmission),
    method: "POST",
    path: `/api/sessions/${sessionId}/message-submissions`,
    body: selectedSubmission,
    response: selectedReceipt,
    expected: selectedReceipt,
  },
  {
    name: "selection-locked refusal preserves the exact submitted selection",
    call: (client) => client.getMessageReceipt(sessionId, submissionId),
    method: "GET",
    path: `/api/sessions/${sessionId}/message-submissions/${submissionId}`,
    response: {
      ...selectedReceipt,
      acceptance: "refused",
      refusalReason: "selectionLocked",
      executionId: null,
    },
    expected: {
      ...selectedReceipt,
      acceptance: "refused",
      refusalReason: "selectionLocked",
      executionId: null,
    },
  },
  {
    name: "active snapshot preserves the latest initiating selection",
    call: (client) => client.getSnapshot(sessionId),
    method: "GET",
    path: `/api/sessions/${sessionId}/snapshot`,
    response: { ...snapshot, latestLlmSelection: selection },
    expected: { ...snapshot, latestLlmSelection: selection },
  },
  {
    name: "settled snapshot retains the latest initiating selection",
    call: (client) => client.getSnapshot(sessionId),
    method: "GET",
    path: `/api/sessions/${sessionId}/snapshot`,
    response: {
      ...snapshot,
      unsettledExecutionId: null,
      lastSettlement: { executionId, outcome: "completed" },
      latestLlmSelection: selection,
    },
    expected: {
      ...snapshot,
      unsettledExecutionId: null,
      lastSettlement: { executionId, outcome: "completed" },
      latestLlmSelection: selection,
    },
  },
  {
    name: "receipt",
    call: (client) => client.getMessageReceipt(sessionId, submissionId),
    method: "GET",
    path: `/api/sessions/${sessionId}/message-submissions/${submissionId}`,
    response: receipt,
    expected: receipt,
  },
  {
    name: "Stop sends an empty JSON object",
    call: (client) => client.stopExecution(sessionId, executionId),
    method: "POST",
    path: `/api/sessions/${sessionId}/executions/${executionId}/stop`,
    body: {},
    response: stop,
    expected: stop,
  },
];

// Node owns top-level test completion; only the registration promise is intentionally ignored.
for (const mapping of mappings) {
  void test(mapping.name, { timeout: 5_000 }, async (t) => {
    const server = await startHttpServer((_request, response) => {
      response.writeHead(mapping.name.startsWith("create") ? 201 : 200, {
        "Content-Type": "application/json",
      });
      response.end(JSON.stringify(mapping.response));
    });
    t.after(() => server.close());

    assert.deepEqual(await mapping.call(new SessionClient(server.origin)), mapping.expected);
    assert.equal(server.requests.length, 1);
    const request = server.requests[0];
    assert.ok(request);
    assert.equal(request.method, mapping.method);
    assert.equal(request.headers["x-urd-client"], "browser");
    const url = new URL(request.url, server.origin);
    assert.equal(url.pathname, mapping.path);
    assert.deepEqual(
      [...url.searchParams.entries()].sort(),
      Object.entries(mapping.query ?? {}).sort(),
    );
    if (mapping.body === undefined) {
      assert.equal(request.body, "");
    } else {
      assert.match(request.headers["content-type"] ?? "", /^application\/json\b/i);
      assert.equal(request.body, JSON.stringify(mapping.body));
    }
  });
}

void test("construction does no I/O and every session operation retains browser transport options", async (t) => {
  const fetchMock = t.mock.method(
    globalThis,
    "fetch",
    async () =>
      new Response(JSON.stringify({ sessionId }), {
        headers: { "Content-Type": "application/json" },
      }),
  );
  const client = new SessionClient(new URL("https://server.example/ignored/path?ignored=query"));
  assert.equal(fetchMock.mock.callCount(), 0);

  for (const mapping of mappings) {
    await mapping.call(client);
    const call = fetchMock.mock.calls.at(-1);
    assert.ok(call);
    const [url, options] = call.arguments;
    assert.ok(url instanceof URL);
    assert.equal(url.origin, "https://server.example");
    assert.equal(url.pathname, mapping.path);
    assert.deepEqual(options, {
      method: mapping.method,
      headers: {
        "X-Urd-Client": "browser",
        Accept: "application/json",
        ...(mapping.method === "POST" ? { "Content-Type": "application/json" } : {}),
      },
      cache: "no-store",
      credentials: "omit",
      redirect: "error",
      ...(mapping.body === undefined ? {} : { body: JSON.stringify(mapping.body) }),
    });
  }
  assert.equal(fetchMock.mock.callCount(), mappings.length);
});

void test("a missing receipt is null, not a refused submission", { timeout: 5_000 }, async (t) => {
  const refused: UserMessageReceipt = {
    ...receipt,
    acceptance: "refused",
    refusalReason: "staleRevision",
    executionId: null,
  };
  const responses = [null, refused];
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

for (const failure of [
  {
    name: "JSON",
    status: 409,
    type: "application/json",
    body: '{"reason":"conflict"}',
    expected: { reason: "conflict" },
  },
  {
    name: "text",
    status: 503,
    type: "text/plain",
    body: "temporarily unavailable",
    expected: "temporarily unavailable",
  },
  {
    name: "malformed JSON",
    status: 502,
    type: "application/json",
    body: '{"broken":',
    expected: '{"broken":',
  },
  { name: "empty body", status: 404, type: "text/plain", body: "", expected: "" },
]) {
  void test(`HTTP errors preserve status and ${failure.name}`, { timeout: 5_000 }, async (t) => {
    const server = await startHttpServer((_request, response) => {
      response.writeHead(failure.status, { "Content-Type": failure.type });
      response.end(failure.body);
    });
    t.after(() => server.close());

    await assert.rejects(
      new SessionClient(server.origin).getMessageReceipt(sessionId, submissionId),
      (error) => {
        assert.ok(error instanceof RequestError);
        assert.equal(error.status, failure.status);
        assert.deepEqual(error.responseBody, failure.expected);
        return true;
      },
    );
    assert.equal(server.requests.length, 1);
  });
}

for (const success of [
  { name: "non-JSON", type: "text/plain", body: "not JSON" },
  {
    name: "JSON under a non-JSON content type",
    type: "text/plain",
    body: '{"sessionIds":[],"nextCursor":null}',
  },
  { name: "malformed JSON", type: "application/json", body: '{"sessionIds":' },
  { name: "empty JSON", type: "application/json", body: "" },
]) {
  void test(`${success.name} success rejects`, { timeout: 5_000 }, async (t) => {
    const server = await startHttpServer((_request, response) => {
      response.writeHead(200, { "Content-Type": success.type });
      response.end(success.body);
    });
    t.after(() => server.close());
    await assert.rejects(new SessionClient(server.origin).list());
    assert.equal(server.requests.length, 1);
  });
}

for (const invalid of [
  {},
  { sessionId: null },
  { sessionId: 42 },
  { sessionId: "" },
  { sessionId: "not-a-session-id" },
  { sessionId: "00000000-0000-0000-0000-000000000000" },
]) {
  void test(
    `create rejects an invalid identity: ${JSON.stringify(invalid)}`,
    { timeout: 5_000 },
    async (t) => {
      const server = await startHttpServer((_request, response) => {
        response.writeHead(201, { "Content-Type": "application/json" });
        response.end(JSON.stringify(invalid));
      });
      t.after(() => server.close());
      await assert.rejects(new SessionClient(server.origin).create());
      assert.equal(server.requests.length, 1);
    },
  );
}

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
  },
);

void test("redirects cannot silently resend a command", { timeout: 5_000 }, async (t) => {
  const server = await startHttpServer((request, response) => {
    if (request.url === "/redirect-target") {
      response.writeHead(200, { "Content-Type": "application/json" });
      response.end(JSON.stringify(receipt));
      return;
    }
    response.writeHead(307, { Location: "/redirect-target" });
    response.end();
  });
  t.after(() => server.close());

  await assert.rejects(new SessionClient(server.origin).submitMessage(sessionId, submission));
  assert.equal(server.requests.length, 1);
});

const commands: { name: string; call: Operation }[] = [
  { name: "create", call: (client) => client.create() },
  { name: "submit", call: (client) => client.submitMessage(sessionId, submission) },
  { name: "Stop", call: (client) => client.stopExecution(sessionId, executionId) },
];
for (const command of commands) {
  for (const phase of ["request", "acknowledgement"] as const) {
    void test(
      `${command.name} never retries after a ${phase} disconnect`,
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
