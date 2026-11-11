import assert from "node:assert/strict";
import type { ServerResponse } from "node:http";
import test from "node:test";
import { RequestError } from "../http/RequestError.ts";
import {
  MarkdownClient,
  type MarkdownOwnedTextGeneration,
} from "../markdown-rendering/MarkdownClient.ts";
import type { TranscriptEntryRef } from "../sessions/Messages.ts";
import { startHttpServer } from "./HttpTestServer.ts";

const generation = "00000000-0000-4000-8000-000000000001";
const sessionId = "00000000-0000-4000-8000-000000000002";
const executionId = "00000000-0000-4000-8000-000000000003";
const entryId = "00000000-0000-4000-8000-000000000004";
const messageId = "00000000-0000-4000-8000-000000000005";
const streamId = "00000000-0000-4000-8000-000000000006";
const entry: TranscriptEntryRef = {
  executionId,
  entryId,
  kind: "text",
  historyRevision: 7,
};
const manifest = {
  generation,
  sourceUtf8Bytes: 4,
  pieceCount: 2,
  contextCount: 1,
};
const sourceFacts = {
  from: {
    message: { executionId, messageId, role: "assistant", historyRevision: 7 },
    segmentIndex: 0,
    utf8Offset: 0,
  },
  availableThrough: {
    message: { executionId, messageId, role: "assistant", historyRevision: 7 },
    segmentIndex: 0,
    utf8Offset: 4,
  },
  isClosed: true,
  finalOutputCommitted: true,
};

function context(overrides: Record<string, unknown> = {}) {
  return {
    id: 0,
    kind: 0,
    parent: -1,
    firstChild: -1,
    nextSibling: -1,
    childCount: 0,
    source: { start: 0, end: 4 },
    firstPiece: 0,
    pieceCount: 2,
    firstValue: 0,
    valueCount: 1,
    position: 0,
    level: 0,
    ordered: false,
    loose: false,
    header: false,
    alignment: 2,
    headerCell: -1,
    ...overrides,
  };
}

function opened() {
  return { manifest, source: sourceFacts };
}

function piece(overrides: Record<string, unknown> = {}) {
  return {
    id: 0,
    context: 0,
    kind: 0,
    text: "Hi",
    source: { start: 0, end: 2 },
    mapping: 0,
    part: 0,
    continued: false,
    ...overrides,
  };
}

function sendJson(response: ServerResponse, value: unknown, status = 200) {
  response.writeHead(status, { "Content-Type": "application/json" });
  response.end(JSON.stringify(value));
}

function isRoute(
  request: { method: string; url: string },
  method: string,
  suffix: string,
): boolean {
  return (
    request.method === method &&
    new URL(request.url, "http://server.example").pathname.endsWith(suffix)
  );
}

void test("maps lease and page routes, bodies, and numeric Markdown enums", async (t) => {
  const server = await startHttpServer((request, response) => {
    if (isRoute(request, "POST", "/release")) {
      response.writeHead(204);
      response.end();
    } else if (isRoute(request, "POST", "/locate")) {
      sendJson(response, { generation, index: 0 });
    } else if (request.url.includes("/source?")) {
      sendJson(response, sourceFacts.from);
    } else if (request.method === "POST" && !request.url.endsWith("/contexts")) {
      sendJson(response, opened());
    } else if (request.url.includes("/locate?")) {
      sendJson(response, { generation, index: 0 });
    } else if (request.url.includes("/pieces/before?")) {
      sendJson(response, {
        generation,
        from: 0,
        next: 1,
        hasMore: true,
        pieces: [
          {
            id: 0,
            context: 0,
            kind: 0,
            text: "Hi",
            source: { start: 0, end: 2 },
            mapping: 0,
            part: 0,
            continued: false,
          },
        ],
      });
    } else if (request.url.includes("/pieces?")) {
      sendJson(response, {
        generation,
        from: 0,
        next: 1,
        hasMore: true,
        pieces: [
          {
            id: 0,
            context: 0,
            kind: 0,
            text: "Hi",
            source: { start: 0, end: 2 },
            mapping: 0,
            part: 0,
            continued: false,
          },
        ],
      });
    } else if (request.url.includes("/contexts/0/children/1")) {
      sendJson(response, { generation, index: 2 });
    } else if (request.url.includes("/contexts/0?")) {
      sendJson(response, {
        generation,
        context: context(),
        from: 0,
        next: 1,
        hasMore: false,
        values: [{ kind: 0, text: "destination", part: 0, continued: false }],
      });
    } else if (request.url.endsWith("/contexts")) {
      sendJson(response, { generation, from: 0, next: 1, hasMore: false, contexts: [context()] });
    } else {
      response.writeHead(404);
      response.end();
    }
  });
  t.after(() => server.close());

  const client = new MarkdownClient(server.origin);
  const signal = new AbortController().signal;
  const owned = await client.request(streamId, sessionId, entry).open(signal);
  const cursor = await owned.locate(1, signal);
  const pieces = await owned.readPieces(cursor, signal);
  const preceding = await owned.readPiecesBefore({ generation, index: 1 }, signal);
  const contextPage = await owned.readContext(0, { generation, index: 0 }, signal);
  const childCursor = await owned.locateChild(0, 1, signal);
  const descriptors = await owned.readContexts([0], { generation, index: 0 }, signal);
  const saved = await owned.sourceCursor(1, signal);
  assert.deepEqual(saved, sourceFacts.from);
  assert.deepEqual(await owned.locateSource(saved, signal), cursor);

  assert.deepEqual(owned.source, sourceFacts);
  assert.equal(pieces.pieces[0]?.kind, "text");
  assert.equal(pieces.pieces[0]?.mapping, "exact");
  assert.equal(preceding.pieces[0]?.kind, "text");
  assert.equal(contextPage.context.kind, "document");
  assert.equal(contextPage.context.alignment, "center");
  assert.deepEqual(childCursor, { generation, index: 2 });
  assert.equal(contextPage.values[0]?.kind, "destination");
  assert.equal(descriptors.contexts[0]?.kind, "document");

  await Promise.all([owned.dispose(), owned.dispose()]);
  assert.equal(server.requests.filter((request) => request.url.endsWith("/release")).length, 1);
  await assert.rejects(owned.readPieces(cursor, signal), /disposed/u);
  await assert.rejects(owned.sourceCursor(1, signal), /disposed/u);
  await assert.rejects(owned.locateSource(saved, signal), /disposed/u);

  const open = server.requests[0]!;
  const leasePath = new URL(open.url, server.origin).pathname;
  const leaseId = leasePath.split("/").at(-1)!;
  assert.match(leaseId, /^[\da-f]{8}-(?:[\da-f]{4}-){3}[\da-f]{12}$/iu);
  assert.notEqual(leaseId, generation);
  assert.equal(open.method, "POST");
  assert.equal(leasePath, `/api/events/${streamId}/markdown/${leaseId}`);
  assert.deepEqual(JSON.parse(open.body), { sessionId, entry });
  assert.deepEqual(
    server.requests.map(({ method, url }) => `${method} ${url}`),
    [
      `POST ${leasePath}`,
      `GET ${leasePath}/locate?sourceUtf8Offset=1`,
      `GET ${leasePath}/pieces?generation=${generation}&index=0`,
      `GET ${leasePath}/pieces/before?generation=${generation}&index=1`,
      `GET ${leasePath}/contexts/0?generation=${generation}&index=0`,
      `GET ${leasePath}/contexts/0/children/1`,
      `POST ${leasePath}/contexts`,
      `GET ${leasePath}/source?sourceUtf8Offset=1`,
      `POST ${leasePath}/locate`,
      `POST ${leasePath}/release`,
    ],
  );
  assert.deepEqual(JSON.parse(server.requests[6]!.body), {
    generation,
    index: 0,
    ids: [0],
  });
  assert.deepEqual(JSON.parse(server.requests.at(-2)!.body), saved);
  assert.deepEqual(JSON.parse(server.requests.at(-1)!.body), {});
});

interface InvalidWirePageCase {
  readonly name: string;
  readonly response: unknown;
  read(source: MarkdownOwnedTextGeneration, signal: AbortSignal): Promise<unknown>;
  readonly expected: RegExp;
}

const invalidWirePages: readonly InvalidWirePageCase[] = [
  {
    name: "piece kind index",
    response: { generation, from: 0, next: 1, hasMore: true, pieces: [piece({ kind: 6 })] },
    read: (source, signal) => source.readPieces({ generation, index: 0 }, signal),
    expected: /unknown Markdown text kind index/u,
  },
  {
    name: "source mapping index",
    response: { generation, from: 0, next: 1, hasMore: true, pieces: [piece({ mapping: 3 })] },
    read: (source, signal) => source.readPieces({ generation, index: 0 }, signal),
    expected: /unknown Markdown source mapping index/u,
  },
  {
    name: "context kind index",
    response: { generation, from: 0, next: 1, hasMore: false, contexts: [context({ kind: 16 })] },
    read: (source, signal) => source.readContexts([0], { generation, index: 0 }, signal),
    expected: /unknown Markdown context kind index/u,
  },
  {
    name: "alignment index",
    response: {
      generation,
      context: context({ alignment: 4 }),
      from: 0,
      next: 1,
      hasMore: false,
      values: [],
    },
    read: (source, signal) => source.readContext(0, { generation, index: 0 }, signal),
    expected: /unknown Markdown table alignment index/u,
  },
  {
    name: "value kind index",
    response: {
      generation,
      context: context(),
      from: 0,
      next: 1,
      hasMore: false,
      values: [{ kind: 5, text: "", part: 0, continued: false }],
    },
    read: (source, signal) => source.readContext(0, { generation, index: 0 }, signal),
    expected: /unknown Markdown value kind index/u,
  },
  {
    name: "page from another generation",
    response: {
      generation: "00000000-0000-4000-8000-000000000099",
      from: 0,
      next: 1,
      hasMore: true,
      pieces: [piece()],
    },
    read: (source, signal) => source.readPieces({ generation, index: 0 }, signal),
    expected: /generation changed/u,
  },
];

for (const invalid of invalidWirePages) {
  void test(`rejects an unknown ${invalid.name} response`, async (t) => {
    const server = await startHttpServer((request, response) => {
      if (request.url.endsWith("/release")) {
        response.writeHead(204);
        response.end();
      } else if (request.method === "POST" && !request.url.endsWith("/contexts"))
        sendJson(response, opened());
      else sendJson(response, invalid.response);
    });
    t.after(() => server.close());

    const client = new MarkdownClient(server.origin);
    const signal = new AbortController().signal;
    const source = await client.request(streamId, sessionId, entry).open(signal);
    await assert.rejects(invalid.read(source, signal), invalid.expected);
    assert.equal(server.requests.length, 2);
    await source.dispose();
  });
}

for (const releasedStatus of [404, 410]) {
  void test(`treats HTTP ${releasedStatus} as an already released Markdown lease`, async (t) => {
    const server = await startHttpServer((request, response) => {
      if (request.url.endsWith("/release")) {
        response.writeHead(releasedStatus);
        response.end();
      } else sendJson(response, opened());
    });
    t.after(() => server.close());

    const source = await new MarkdownClient(server.origin)
      .request(streamId, sessionId, entry)
      .open(new AbortController().signal);
    await source.dispose();
    await source.dispose();
    assert.equal(server.requests.length, 2);
  });
}

for (const failure of [
  {
    name: "HTTP failure",
    respond(response: ServerResponse) {
      sendJson(response, { error: "failed" }, 503);
    },
    expected: RequestError,
  },
  {
    name: "malformed JSON",
    respond(response: ServerResponse) {
      response.writeHead(200, { "Content-Type": "application/json" });
      response.end("{broken");
    },
    expected: SyntaxError,
  },
  {
    name: "malformed open payload",
    respond(response: ServerResponse) {
      sendJson(response, { manifest });
    },
    expected: TypeError,
  },
]) {
  void test(`releases the client lease after ${failure.name}`, async (t) => {
    const server = await startHttpServer((request, response) => {
      if (isRoute(request, "POST", "/release")) {
        response.writeHead(204);
        response.end();
      } else failure.respond(response);
    });
    t.after(() => server.close());

    await assert.rejects(
      new MarkdownClient(server.origin)
        .request(streamId, sessionId, entry)
        .open(new AbortController().signal),
      failure.expected,
    );
    assert.equal(server.requests.length, 2);
    const openPath = new URL(server.requests[0]!.url, server.origin).pathname;
    const releasePath = new URL(server.requests[1]!.url, server.origin).pathname;
    assert.equal(releasePath, `${openPath}/release`);
  });
}

void test("aborted opens release the known lease before rejecting", async (t) => {
  let sawOpen!: () => void;
  const openSeen = new Promise<void>((resolve) => (sawOpen = resolve));
  const server = await startHttpServer((request, response) => {
    if (request.url.endsWith("/release")) {
      response.writeHead(204);
      response.end();
    } else sawOpen();
  });
  t.after(() => server.close());

  const controller = new AbortController();
  const opening = new MarkdownClient(server.origin)
    .request(streamId, sessionId, entry)
    .open(controller.signal);
  await openSeen;
  controller.abort();
  await assert.rejects(opening, { name: "AbortError" });
  assert.equal(server.requests.length, 2);
  assert.equal(server.requests[1]!.url.endsWith("/release"), true);
});

void test("waits for failed-open lease cleanup and does not hide cleanup failure", async (t) => {
  let releaseResponse!: ServerResponse;
  let sawRelease!: () => void;
  const releaseSeen = new Promise<void>((resolve) => (sawRelease = resolve));
  const server = await startHttpServer((request, response) => {
    if (request.url.endsWith("/release")) {
      releaseResponse = response;
      sawRelease();
    } else sendJson(response, { error: "open failed" }, 503);
  });
  t.after(() => server.close());

  const opening = new MarkdownClient(server.origin)
    .request(streamId, sessionId, entry)
    .open(new AbortController().signal);
  let settled = false;
  void opening.then(
    () => (settled = true),
    () => (settled = true),
  );
  await releaseSeen;
  await new Promise<void>((resolve) => setImmediate(resolve));
  assert.equal(settled, false);
  releaseResponse.writeHead(500, { "Content-Type": "application/json" });
  releaseResponse.end("{}");
  await assert.rejects(opening, (error: unknown) => {
    assert.ok(error instanceof AggregateError);
    assert.equal(error.errors.length, 2);
    assert.ok(error.errors[0] instanceof RequestError);
    assert.ok(error.errors[1] instanceof RequestError);
    assert.equal((error.errors[1] as RequestError).status, 500);
    return true;
  });
  assert.equal(settled, true);
});

interface InvalidReadCase {
  readonly name: string;
  run(source: MarkdownOwnedTextGeneration, signal: AbortSignal): Promise<unknown>;
  readonly expected: RegExp;
}

const invalidReads: readonly InvalidReadCase[] = [
  {
    name: "different generation",
    run(source, signal) {
      return source.readPieces({ generation: "different", index: 0 }, signal);
    },
    expected: /generation changed/u,
  },
  {
    name: "piece index beyond manifest",
    run(source, signal) {
      return source.readPieces({ generation, index: manifest.pieceCount + 1 }, signal);
    },
    expected: /index/u,
  },
  {
    name: "unknown context ID",
    run(source, signal) {
      return source.readContext(manifest.contextCount, { generation, index: 0 }, signal);
    },
    expected: /context ID/u,
  },
  {
    name: "duplicate context IDs",
    run(source, signal) {
      return source.readContexts([0, 0], { generation, index: 0 }, signal);
    },
    expected: /distinct/u,
  },
];

void test("rejects invalid generations and indices before making read requests", async (t) => {
  const server = await startHttpServer((request, response) => {
    if (request.url.endsWith("/release")) {
      response.writeHead(204);
      response.end();
    } else sendJson(response, opened());
  });
  t.after(() => server.close());

  const client = new MarkdownClient(server.origin);
  const signal = new AbortController().signal;
  const source = await client.request(streamId, sessionId, entry).open(signal);
  for (const invalid of invalidReads)
    await assert.rejects(invalid.run(source, signal), invalid.expected, invalid.name);
  assert.equal(server.requests.length, 1);
  await source.dispose();
});
