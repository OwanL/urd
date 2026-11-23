import assert from "node:assert/strict";
import test from "node:test";
import type {
  TranscriptEntryRef,
  TranscriptPositionWindow,
} from "../../../../sessions/transcript/Messages.ts";
import { TranscriptPositionReader } from "../../../../chat/transcript/reading/TranscriptPositionReader.ts";

interface Deferred<T> {
  promise: Promise<T>;
  resolve(value: T): void;
  reject(cause: unknown): void;
}

function deferred<T>(): Deferred<T> {
  let resolve!: (value: T) => void;
  let reject!: (cause: unknown) => void;
  const promise = new Promise<T>((complete, fail) => {
    resolve = complete;
    reject = fail;
  });
  return { promise, resolve, reject };
}

type PositionReadCall =
  | {
      readonly kind: "entry";
      readonly sessionId: string;
      readonly entry: TranscriptEntryRef;
      readonly signal: AbortSignal;
      readonly response: Deferred<TranscriptPositionWindow>;
    }
  | {
      readonly kind: "fraction";
      readonly sessionId: string;
      readonly historyRevision: number;
      readonly fraction: number;
      readonly signal: AbortSignal;
      readonly response: Deferred<TranscriptPositionWindow>;
    };

function fakeClient() {
  const calls: PositionReadCall[] = [];
  const client = {
    getEntryPosition(sessionId: string, entry: TranscriptEntryRef, signal?: AbortSignal) {
      const response = deferred<TranscriptPositionWindow>();
      calls.push({ kind: "entry", sessionId, entry, signal: signal!, response });
      return response.promise;
    },
    locatePosition(
      sessionId: string,
      historyRevision: number,
      fraction: number,
      signal?: AbortSignal,
    ) {
      const response = deferred<TranscriptPositionWindow>();
      calls.push({
        kind: "fraction",
        sessionId,
        historyRevision,
        fraction,
        signal: signal!,
        response,
      });
      return response.promise;
    },
  };
  return { calls, client };
}

function positionWindow(historyRevision: number): TranscriptPositionWindow {
  return {
    state: {
      sessionId: "session-a",
      historyRevision,
      positionVersion: 1,
      entryCount: 0,
      totalWeight: 0,
    },
    target: null,
    entries: [],
    toolLabels: {},
    hasBefore: false,
    hasAfter: false,
    settlements: [],
  };
}

const entry: TranscriptEntryRef = {
  executionId: "execution-a",
  entryId: "entry-a",
  kind: "text",
  historyRevision: 7,
};

void test("reads entry and fraction targets only when requested", async () => {
  const { calls, client } = fakeClient();
  const reader = new TranscriptPositionReader(client, "session-a");
  assert.equal(calls.length, 0, "construction does not start a read");

  const restored = positionWindow(7);
  const entryRead = reader.read({ kind: "entry", entry });
  assert.equal(calls[0]?.kind, "entry");
  assert.equal(calls[0]?.sessionId, "session-a");
  if (calls[0]?.kind !== "entry") assert.fail("Expected an entry lookup");
  assert.equal(calls[0].entry, entry);
  calls[0].response.resolve(restored);
  assert.equal(await entryRead, restored);

  const sought = positionWindow(8);
  const fractionRead = reader.read({ kind: "fraction", historyRevision: 8, fraction: 0.625 });
  assert.equal(calls[1]?.kind, "fraction");
  if (calls[1]?.kind !== "fraction") assert.fail("Expected a fraction lookup");
  assert.equal(calls[1].historyRevision, 8);
  assert.equal(calls[1].fraction, 0.625);
  calls[1].response.resolve(sought);
  assert.equal(await fractionRead, sought);
});

void test("stream notices let a slow window publish and coalesce into one latest read", async () => {
  const { calls, client } = fakeClient();
  const reader = new TranscriptPositionReader(client, "session-a");
  const target = { kind: "fraction", historyRevision: 7, fraction: 1 } as const;
  const first = reader.read(target, "1");
  for (const notice of ["2", "3", "4"]) assert.equal(reader.shouldRefresh(7, notice), false);
  assert.equal(calls[0]?.signal.aborted, false);

  const intermediate = positionWindow(7);
  calls[0]!.response.resolve(intermediate);
  assert.equal(await first, intermediate);
  assert.equal(reader.isCurrent(first), true);
  assert.equal(reader.shouldRefresh(7, "1"), false);
  assert.equal(reader.shouldRefresh(7, "4"), true);

  const followUp = reader.read(target, "4");
  const latest = positionWindow(7);
  calls[1]!.response.resolve(latest);
  assert.equal(await followUp, latest);
  assert.equal(reader.shouldRefresh(7, "4"), false);
  assert.equal(calls.length, 2);
});

void test("explicit navigation and a changed revision can supersede a busy window", async () => {
  const { calls, client } = fakeClient();
  const reader = new TranscriptPositionReader(client, "session-a");
  const oldRead = reader.read({ kind: "fraction", historyRevision: 7, fraction: 0.4 }, "1");
  assert.equal(reader.shouldRefresh(7, "2"), false);
  const seek = reader.read({ kind: "entry", entry }, "2");
  assert.equal(reader.shouldRefresh(7, "3"), false);
  assert.equal(reader.shouldRefresh(8, "3"), true);
  const currentRead = reader.read({ kind: "fraction", historyRevision: 8, fraction: 0.6 }, "3");
  assert.equal(reader.shouldRefresh(8, "4"), false);

  for (const call of calls.slice(0, 2)) {
    assert.equal(call.signal.aborted, true);
    call.response.resolve(positionWindow(7));
  }
  assert.equal(await oldRead, null);
  assert.equal(await seek, null);
  assert.equal(reader.shouldRefresh(8, "4"), false, "superseded reads cannot release a refresh");

  const current = positionWindow(8);
  calls[2]!.response.resolve(current);
  assert.equal(await currentRead, current);
  assert.equal(reader.shouldRefresh(8, "3"), false);
  assert.equal(reader.shouldRefresh(8, "4"), true, "only the current read releases the refresh");
});

void test("a settled response is no longer publishable if a newer read starts first", async () => {
  const { calls, client } = fakeClient();
  const reader = new TranscriptPositionReader(client, "session-a");
  const oldRead = reader.read({ kind: "fraction", historyRevision: 7, fraction: 0.4 });
  const oldWindow = positionWindow(7);
  calls[0]!.response.resolve(oldWindow);
  let currentRead: Promise<TranscriptPositionWindow | null> | null = null;
  queueMicrotask(() => {
    currentRead = reader.read({ kind: "fraction", historyRevision: 7, fraction: 0.6 });
  });

  assert.equal(await oldRead, oldWindow);
  assert.equal(reader.isCurrent(oldRead), false);
  assert.ok(currentRead);
  const currentWindow = positionWindow(7);
  calls[1]!.response.resolve(currentWindow);
  assert.equal(await currentRead, currentWindow);
});

void test("cancellation after settlement prevents publication and allows a fresh read", async () => {
  const { calls, client } = fakeClient();
  const reader = new TranscriptPositionReader(client, "session-a");
  const oldRead = reader.read({ kind: "fraction", historyRevision: 7, fraction: 0.4 });
  const oldWindow = positionWindow(7);
  calls[0]!.response.resolve(oldWindow);
  queueMicrotask(() => reader.cancel());

  assert.equal(await oldRead, oldWindow);
  assert.equal(reader.isCurrent(oldRead), false);

  const resumedRead = reader.read({ kind: "fraction", historyRevision: 7, fraction: 0.6 });
  const resumedWindow = positionWindow(7);
  calls[1]!.response.resolve(resumedWindow);
  assert.equal(await resumedRead, resumedWindow);
  assert.equal(reader.isCurrent(resumedRead), true);
});

void test("a settled failure from a superseded read is no longer current", async () => {
  const { calls, client } = fakeClient();
  const reader = new TranscriptPositionReader(client, "session-a");
  const oldRead = reader.read({ kind: "fraction", historyRevision: 7, fraction: 0.4 });
  const failure = new Error("old position read failed");
  const rejected = assert.rejects(oldRead, (cause) => cause === failure);
  calls[0]!.response.reject(failure);
  let currentRead: Promise<TranscriptPositionWindow | null> | null = null;
  queueMicrotask(() => {
    currentRead = reader.read({ kind: "fraction", historyRevision: 7, fraction: 0.6 });
  });

  await rejected;
  assert.equal(reader.isCurrent(oldRead), false);
  assert.ok(currentRead);
  const currentWindow = positionWindow(7);
  calls[1]!.response.resolve(currentWindow);
  assert.equal(await currentRead, currentWindow);
});

void test("cancel aborts a pending read, while a current failure remains visible", async () => {
  const { calls, client } = fakeClient();
  const reader = new TranscriptPositionReader(client, "session-a");
  const pendingRead = reader.read({ kind: "fraction", historyRevision: 7, fraction: 1 }, "1");
  assert.equal(reader.shouldRefresh(7, "2"), false);
  reader.cancel();

  assert.equal(calls[0]?.signal.aborted, true);
  assert.equal(reader.isCurrent(pendingRead), false);
  calls[0]!.response.reject(new DOMException("Aborted", "AbortError"));
  assert.equal(await pendingRead, null);
  assert.equal(reader.shouldRefresh(7, "2"), true, "resume can start a fresh read");

  const failure = new Error("position read failed");
  const currentRead = reader.read({ kind: "entry", entry });
  calls[1]!.response.reject(failure);
  await assert.rejects(currentRead, (cause) => cause === failure);
  assert.equal(reader.isCurrent(currentRead), true);
  assert.equal(reader.shouldRefresh(7, "new notice"), true);
});
