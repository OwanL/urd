import assert from "node:assert/strict";
import test from "node:test";
import { isTextEntry } from "./Messages.ts";
import {
  DebouncedTranscriptNavigationWriter,
  TranscriptNavigationStorage,
  type TranscriptNavigationState,
  type TranscriptNavigationStorageLike,
} from "./TranscriptNavigationStorage.ts";

class MemoryStorage implements TranscriptNavigationStorageLike {
  readonly values = new Map<string, string>();
  getItem(key: string): string | null {
    return this.values.get(key) ?? null;
  }
  setItem(key: string, value: string): void {
    this.values.set(key, value);
  }
  removeItem(key: string): void {
    this.values.delete(key);
  }
}

const sample: TranscriptNavigationState = {
  historyRevision: 3,
  positionVersion: 8,
  followsLatest: false,
  anchor: {
    entry: { executionId: "run", entryId: "entry", kind: "text", historyRevision: 3 },
    sourceCursor: { segmentIndex: 2, utf8Offset: 40 },
    viewportOffset: -12,
  },
  width: 800,
  profileVersion: "compact-v2",
};

void test("persists only the navigation schema and roundtrips valid state", () => {
  const storage = new MemoryStorage();
  const writer = new TranscriptNavigationStorage(storage);
  const state = {
    ...sample,
    unexpected: "top-level",
    anchor: {
      ...sample.anchor!,
      unexpected: "anchor",
      entry: { ...sample.anchor!.entry, unexpected: "entry" },
      sourceCursor: { ...sample.anchor!.sourceCursor!, unexpected: "cursor" },
    },
  } as TranscriptNavigationState;
  assert.equal(writer.save("session/a", state).error, null);

  const persisted = storage.getItem("urd.transcript-navigation.v1.session%2Fa");
  assert.ok(persisted);
  assert.deepEqual(JSON.parse(persisted), { version: 1, state: sample });
  assert.deepEqual(new TranscriptNavigationStorage(storage).load("session/a").value.state, sample);
});

void test("completion anchors roundtrip without a text cursor and stay non-text", () => {
  const storage = new TranscriptNavigationStorage(new MemoryStorage());
  const state: TranscriptNavigationState = {
    ...sample,
    anchor: {
      ...sample.anchor!,
      entry: { ...sample.anchor!.entry, kind: "toolCompletion" },
      sourceCursor: null,
    },
  };
  assert.equal(storage.save("s", state).error, null);
  assert.deepEqual(storage.load("s").value.state, state);
  assert.equal(isTextEntry(state.anchor!.entry), false);
  assert.equal(isTextEntry(sample.anchor!.entry), true);
  assert.equal(isTextEntry({ ...sample.anchor!.entry, kind: "tool" }), false);
  assert.notEqual(
    storage.save("s", {
      ...state,
      anchor: { ...state.anchor!, sourceCursor: sample.anchor!.sourceCursor },
    }).error,
    null,
  );
});

void test("latest debounced write wins and flush persists immediately", () => {
  const storage = new TranscriptNavigationStorage(new MemoryStorage());
  const callbacks: (() => void)[] = [];
  const writer = new DebouncedTranscriptNavigationWriter(
    storage,
    (callback) => {
      callbacks.push(callback);
      return callback;
    },
    () => {},
  );
  writer.scheduleWrite("s", sample);
  const next = { ...sample, positionVersion: 9 };
  writer.scheduleWrite("s", next);
  assert.equal(writer.flush().error, null);
  assert.equal(storage.load("s").value.state?.positionVersion, 9);
  assert.equal(callbacks.length, 2);
});

void test("discards malformed and incompatible records", () => {
  const memory = new MemoryStorage();
  const storage = new TranscriptNavigationStorage(memory);
  const key = "urd.transcript-navigation.v1.s";
  memory.values.set(key, "{");
  assert.equal(storage.load("s").value.state, null);
  assert.equal(memory.values.has(key), false);
  memory.values.set(key, JSON.stringify({ version: 99, state: sample }));
  assert.equal(storage.load("s").value.state, null);
  assert.equal(memory.values.has(key), false);
});

void test("width and profile mismatch disable pixel reuse but retain stable position", () => {
  const storage = new TranscriptNavigationStorage(new MemoryStorage());
  storage.save("s", sample);
  assert.equal(storage.load("s", { width: 801 }).value.pixelReuse, false);
  assert.equal(storage.load("s", { profileVersion: "wide-v1" }).value.pixelReuse, false);
  assert.equal(
    storage.load("s", { width: 800, profileVersion: "compact-v2" }).value.pixelReuse,
    true,
  );
  assert.deepEqual(storage.load("s", { width: 801 }).value.state?.anchor, sample.anchor);
});

void test("storage access failures are returned without throwing", () => {
  const unavailable: TranscriptNavigationStorageLike = {
    getItem() {
      throw new Error("blocked");
    },
    setItem() {
      throw new Error("quota");
    },
    removeItem() {
      throw new Error("blocked");
    },
  };
  const storage = new TranscriptNavigationStorage(unavailable);
  assert.match(storage.load("s").error ?? "", /blocked/);
  assert.match(storage.save("s", sample).error ?? "", /quota/);
  assert.match(storage.clear("s").error ?? "", /blocked/);
});
