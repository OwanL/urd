import type { ServerResponse } from "node:http";
import { deflateSync } from "node:zlib";
import { setTimeout as delay } from "node:timers/promises";
import { startHttpServer, type HttpRequest } from "../HttpTestServer.ts";
import { imageKey, markdownFixture } from "./MarkdownFixture.ts";

const generation = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
const executionId = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb";
const pageSize = 30;

function imagePng() {
  const chunk = (name: string, data: Buffer) => {
    const type = Buffer.from(name);
    let crc = 0xffffffff;
    for (const byte of Buffer.concat([type, data])) {
      crc ^= byte;
      for (let bit = 0; bit < 8; bit++) crc = (crc >>> 1) ^ (0xedb88320 & -(crc & 1));
    }
    const length = Buffer.alloc(4);
    const checksum = Buffer.alloc(4);
    length.writeUInt32BE(data.length);
    checksum.writeUInt32BE((crc ^ 0xffffffff) >>> 0);
    return Buffer.concat([length, type, data, checksum]);
  };
  const width = 640,
    height = 320;
  const header = Buffer.alloc(13);
  header.writeUInt32BE(width, 0);
  header.writeUInt32BE(height, 4);
  header[8] = 8;
  header[9] = 2;
  const pixels = Buffer.alloc((width * 3 + 1) * height, 180);
  for (let y = 0; y < height; y++) pixels[y * (width * 3 + 1)] = 0;
  return Buffer.concat([
    Buffer.from("89504e470d0a1a0a", "hex"),
    chunk("IHDR", header),
    chunk("IDAT", deflateSync(pixels)),
    chunk("IEND", Buffer.alloc(0)),
  ]);
}
const imageBytes = imagePng();
const sessionNames = ["history", "markdown", "image"] as const;
export type FixtureSession = (typeof sessionNames)[number];

function id(index: number): string {
  return `00000000-0000-4000-8000-${String(index + 1).padStart(12, "0")}`;
}

interface Row {
  id: string;
  text: string;
}

function descriptor(row: Row) {
  const message = { executionId, messageId: row.id, role: "assistant", historyRevision: 1 };
  const cursor = (utf8Offset: number) => ({ message, segmentIndex: 0, utf8Offset });
  return {
    entry: { executionId, entryId: row.id, kind: "text", historyRevision: 1 },
    createdAt: null,
    text: {
      from: cursor(0),
      availableThrough: cursor(Buffer.byteLength(row.text)),
      isClosed: true,
      finalOutputCommitted: true,
    },
    tool: null,
  };
}

function deferred() {
  let resolve!: () => void;
  const promise = new Promise<void>((done) => {
    resolve = done;
  });
  return { promise, resolve };
}

// Each rule consumes only the first matching non-preflight request.
export async function startTranscriptFixture() {
  const markdown = markdownFixture("markdown");
  const image = markdownFixture("image");
  const rows: Record<FixtureSession, Row[]> = {
    history: Array.from({ length: 320 }, (_, index) => ({
      id: id(index),
      text: `Message ${String(index + 1).padStart(3, "0")}`,
    })),
    markdown: [{ id: id(1000), text: markdown.text }],
    image: [{ id: id(2000), text: image.text }],
  };
  const leases = new Map<string, Row>();
  const rules: {
    match: (url: URL) => boolean;
    received: ReturnType<typeof deferred>;
    release: ReturnType<typeof deferred>;
    fail: boolean;
  }[] = [];
  function send(
    response: ServerResponse,
    status: number,
    contentType: string,
    body: string | Buffer,
  ) {
    if (response.destroyed) return;
    response.writeHead(status, {
      "Content-Type": contentType,
      "Access-Control-Allow-Origin": "*",
    });
    response.end(body);
  }
  function position(session: FixtureSession, index: number) {
    const row = rows[session][index]!;
    return { ...descriptor(row).entry, rank: index, weight: 500, prefixWeight: index * 500 };
  }
  function state(session: FixtureSession) {
    return {
      sessionId: session,
      historyRevision: 1,
      positionVersion: rows[session].length,
      entryCount: rows[session].length,
      totalWeight: rows[session].length * 500,
    };
  }
  function handle(request: HttpRequest, response: ServerResponse, url: URL) {
    const segments = url.pathname.split("/").filter(Boolean);
    const ruleIndex =
      request.method === "OPTIONS" ? -1 : rules.findIndex((rule) => rule.match(url));
    const rule = ruleIndex < 0 ? undefined : rules.splice(ruleIndex, 1)[0];
    function reply(status: number, contentType: string, body: string | Buffer) {
      if (!rule) return send(response, status, contentType, body);
      rule.received.resolve();
      // The response body is already captured, even if an append happens before release.
      void rule.release.promise.then(() => {
        if (rule.fail) send(response, 500, "application/json", '{"error":"Fixture failure"}');
        else send(response, status, contentType, body);
      });
    }
    const json = (data: unknown, status = 200) =>
      reply(status, "application/json", JSON.stringify(data));
    if (request.method === "OPTIONS") {
      response.writeHead(204, {
        "Access-Control-Allow-Origin": "*",
        "Access-Control-Allow-Methods": "GET, POST, OPTIONS",
        "Access-Control-Allow-Headers": "content-type, x-urd-client",
      });
      response.end();
      return;
    }
    if (segments[1] === "sessions") {
      const session = segments[2] as FixtureSession;
      if (!sessionNames.includes(session)) return json({ error: "Unknown session" }, 404);
      const data = rows[session];
      const path = segments.slice(3).join("/");
      if (path === "snapshot")
        return json({
          sessionId: session,
          historyRevision: 1,
          workingDirectory: null,
          canChangeWorkingDirectory: false,
          admissionFenced: false,
          canContinue: false,
          unsettledExecutionId: null,
          stopRequested: false,
          lastSettlement: null,
          latestLlmSelection: null,
          firstUserPreview: null,
          pendingInputs: [],
        });
      if (path === "positions") return json(state(session));
      if (path === "positions/prompts")
        return json({ state: state(session), prompts: [], nextAfterRank: null });
      if (path === "history") {
        const mode = url.searchParams.get("mode");
        const anchor = data.findIndex((row) => row.id === url.searchParams.get("entryId"));
        const start =
          mode === "latest"
            ? Math.max(0, data.length - pageSize)
            : mode === "before"
              ? Math.max(0, anchor - pageSize)
              : mode === "after"
                ? anchor + 1
                : Math.max(0, Math.min(anchor - Math.floor(pageSize / 2), data.length - pageSize));
        const end = mode === "before" ? anchor : Math.min(data.length, start + pageSize);
        return json({
          sessionId: session,
          historyRevision: 1,
          entries: data.slice(start, end).map(descriptor),
          toolLabels: {},
          hasBefore: start > 0,
          hasAfter: end < data.length,
          settlements: [],
        });
      }
      if (path === "positions/locate" || path.startsWith("positions/entries/")) {
        const index =
          path === "positions/locate"
            ? Math.min(
                data.length - 1,
                Math.floor(Number(url.searchParams.get("fraction")) * data.length),
              )
            : data.findIndex((row) => row.id === segments[6]);
        if (index < 0) return json({}, 404);
        const start = Math.max(0, Math.min(index - 15, data.length - pageSize)),
          end = Math.min(data.length, start + pageSize);
        return json({
          state: state(session),
          target: position(session, index),
          entries: data.slice(start, end).map((row, offset) => ({
            descriptor: descriptor(row),
            position: position(session, start + offset),
          })),
          toolLabels: {},
          hasBefore: start > 0,
          hasAfter: end < data.length,
          settlements: [],
        });
      }
      if (path.startsWith("markdown-images/")) {
        if (
          session !== "image" ||
          segments[4] !== executionId ||
          segments[5] !== id(2000) ||
          segments[6] !== imageKey
        )
          return json({}, 404);
        return reply(200, "image/png", imageBytes);
      }
      if (path === "content") {
        const row = data.find((item) => item.id === url.searchParams.get("messageId"));
        if (!row) return json({}, 404);
        const facts = descriptor(row).text;
        return json({
          from: facts.from,
          text: row.text,
          next: facts.availableThrough,
          availableThrough: facts.availableThrough,
          hasMore: false,
          isClosed: true,
          finalOutputCommitted: true,
        });
      }
    }
    if (segments[1] === "events" && segments[3] === "markdown") {
      const lease = segments[4]!;
      const path = segments.slice(5).join("/");
      if (!path && request.method === "POST") {
        const body = JSON.parse(request.body) as {
          sessionId: FixtureSession;
          entry: { entryId: string };
        };
        const row = rows[body.sessionId]?.find((item) => item.id === body.entry.entryId);
        if (!row) return json({}, 404);
        leases.set(lease, row);
        return json({
          manifest: {
            generation,
            sourceUtf8Bytes: Buffer.byteLength(row.text),
            pieceCount: (row.text === markdown.text
              ? markdown
              : row.text === image.text
                ? image
                : markdownFixture("plain", row.text)
            ).pieces.length,
            contextCount: (row.text === markdown.text
              ? markdown
              : row.text === image.text
                ? image
                : markdownFixture("plain", row.text)
            ).contexts.length,
          },
          source: descriptor(row).text,
        });
      }
      if (path === "release") {
        leases.delete(lease);
        response.writeHead(204, { "Access-Control-Allow-Origin": "*" });
        response.end();
        return;
      }
      const row = leases.get(lease);
      if (!row) return json({}, 404);
      const fixture =
        row.text === markdown.text
          ? markdown
          : row.text === image.text
            ? image
            : markdownFixture("plain", row.text);
      const { contexts, pieces, wireContext, wirePiece, wireValues } = fixture;
      const index = Number(url.searchParams.get("index") ?? 0);
      const pageSize = 40;
      if (path === "locate") {
        const offset =
          request.method === "POST"
            ? Number((JSON.parse(request.body) as { utf8Offset: number }).utf8Offset)
            : Number(url.searchParams.get("sourceUtf8Offset"));
        const piece = pieces.findLast((item) => item.source.start <= offset);
        return json({ generation, index: piece?.id ?? 0 });
      }
      if (path === "source")
        return json({
          ...descriptor(row).text.from,
          utf8Offset: Number(url.searchParams.get("sourceUtf8Offset")),
        });
      if (path === "pieces" || path === "pieces/before") {
        const before = path.endsWith("before");
        const from = before ? Math.max(0, index - pageSize) : index;
        const next = before ? index : Math.min(pieces.length, index + pageSize);
        return json({
          generation,
          from,
          next,
          hasMore: next < pieces.length,
          pieces: pieces.slice(from, next).map(wirePiece),
        });
      }
      if (path === "contexts" && request.method === "POST") {
        const body = JSON.parse(request.body) as { ids: number[]; index: number };
        const next = Math.min(body.ids.length, body.index + pageSize);
        return json({
          generation,
          from: body.index,
          next,
          hasMore: next < body.ids.length,
          contexts: body.ids.slice(body.index, next).map((id) => wireContext(contexts[id]!)),
        });
      }
      if (path.startsWith("contexts/")) {
        const context = contexts[Number(segments[6])];
        if (!context) return json({}, 404);
        if (segments[7] === "children") {
          let child = context.firstChild;
          for (let i = 0; i < Number(segments[8]); i++) child = contexts[child]?.nextSibling ?? -1;
          if (child < 0) return json({}, 404);
          return json({ generation, index: contexts[child]!.firstPiece });
        }
        const values = wireValues(context.id);
        const next = Math.min(values.length, index + pageSize);
        return json({
          generation,
          context: wireContext(context),
          from: index,
          next,
          hasMore: next < values.length,
          values: values.slice(index, next),
        });
      }
    }
    return json({ error: `Unhandled ${request.method} ${url.pathname}` }, 404);
  }
  const server = await startHttpServer((request, response) => {
    handle(request, response, new URL(request.url, "http://localhost"));
  });
  return {
    origin: server.origin,
    requests: server.requests,
    append(session: FixtureSession = "history") {
      const index = rows[session].length;
      const row = { id: id(index), text: `Message ${String(index + 1).padStart(3, "0")}` };
      rows[session].push(row);
      return row;
    },
    hold(match: (url: URL) => boolean, fail = false) {
      const rule = { match, received: deferred(), release: deferred(), fail };
      rules.push(rule);
      const timer = new AbortController();
      const received = Promise.race([
        rule.received.promise,
        delay(4_000, undefined, { signal: timer.signal }).then(() => {
          throw new Error("Expected a matching fixture request within 4 seconds.");
        }),
      ]).finally(() => timer.abort());
      return { received, release: rule.release.resolve };
    },
    close: server.close,
  };
}
