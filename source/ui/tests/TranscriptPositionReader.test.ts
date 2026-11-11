import assert from "node:assert/strict";
import test from "node:test";
import type { TranscriptEntryRef, TranscriptPositionWindow } from "../sessions/Messages.ts";
import { TranscriptPositionReader } from "../app/TranscriptPositionReader.ts";

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

void test("a newer read aborts and ignores an older response", async () => {
  const { calls, client } = fakeClient();
  const reader = new TranscriptPositionReader(client, "session-a");
  const oldRead = reader.read({ kind: "fraction", historyRevision: 7, fraction: 0.4 });
  const currentRead = reader.read({ kind: "entry", entry });

  assert.equal(calls[0]?.signal.aborted, true);
  calls[0]!.response.resolve(positionWindow(7));
  assert.equal(await oldRead, null);

  const current = positionWindow(7);
  calls[1]!.response.resolve(current);
  assert.equal(await currentRead, current);
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
  const pendingRead = reader.read({ kind: "fraction", historyRevision: 7, fraction: 1 });
  reader.cancel();

  assert.equal(calls[0]?.signal.aborted, true);
  assert.equal(reader.isCurrent(pendingRead), false);
  calls[0]!.response.reject(new DOMException("Aborted", "AbortError"));
  assert.equal(await pendingRead, null);

  const failure = new Error("position read failed");
  const currentRead = reader.read({ kind: "entry", entry });
  calls[1]!.response.reject(failure);
  await assert.rejects(currentRead, (cause) => cause === failure);
  assert.equal(reader.isCurrent(currentRead), true);
});
