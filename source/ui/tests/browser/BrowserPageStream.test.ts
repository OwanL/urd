import assert from "node:assert/strict";
import { after, before, test, type TestContext } from "node:test";
import { BrowserPageStream } from "../../chat/browser/BrowserPageStream.ts";
import type { BrowserPageStreamState } from "../../chat/browser/BrowserPageStream.ts";

// First-frame retry budget and backoff constants from the stream.
const firstFrameTimeoutMs = 3000;
const reconnectBackoffMs = 500;

class FakeSocket {
  static readonly CONNECTING = 0;
  static readonly OPEN = 1;
  static readonly CLOSING = 2;
  static readonly CLOSED = 3;
  static created: FakeSocket[] = [];

  readonly url: string;
  readyState = FakeSocket.OPEN;
  binaryType = "blob";
  onopen: (() => void) | null = null;
  onclose: ((event: { reason: string }) => void) | null = null;
  onmessage: ((event: { data: unknown }) => void) | null = null;
  sent: string[] = [];
  closeCount = 0;

  constructor(url: string) {
    this.url = url;
    FakeSocket.created.push(this);
  }

  send(data: string): void {
    this.sent.push(data);
  }

  close(): void {
    this.closeCount += 1;
  }
}

interface Recording {
  states: Array<[BrowserPageStreamState, string | null]>;
  frames: Blob[];
}

function openStream(): { stream: BrowserPageStream; recording: Recording; socket: FakeSocket } {
  const recording: Recording = { states: [], frames: [] };
  const stream = new BrowserPageStream("session", "page", {
    frame: (frame) => recording.frames.push(frame),
    stateChange: (state, failure) => recording.states.push([state, failure]),
  });
  stream.open();
  return { stream, recording, socket: FakeSocket.created[0]! };
}

before(() => {
  Object.assign(globalThis, { location: { protocol: "http:", host: "urd.test" } });
});

after(() => {
  delete (globalThis as { location?: unknown }).location;
});

function useFakeSockets(t: TestContext): void {
  const original = globalThis.WebSocket;
  globalThis.WebSocket = FakeSocket as unknown as typeof WebSocket;
  t.after(() => {
    globalThis.WebSocket = original;
  });
}

void test("an open socket delivers binary frames and carries commands", (t) => {
  useFakeSockets(t);
  FakeSocket.created = [];
  const { stream, recording, socket } = openStream();
  socket.onopen!();
  assert.deepEqual(recording.states, [
    ["connecting", null],
    ["open", null],
  ]);
  socket.onmessage!({ data: "unexpected" });
  const frame = new Blob([new Uint8Array([1, 2])]);
  socket.onmessage!({ data: frame });
  assert.deepEqual(recording.frames, [frame]);
  assert.equal(stream.send({ type: "click", x: 3, y: 4 }), true);
  assert.deepEqual(socket.sent, [JSON.stringify({ type: "click", x: 3, y: 4 })]);
  socket.readyState = FakeSocket.CONNECTING;
  assert.equal(stream.send({ type: "click", x: 3, y: 4 }), false);
});

void test("silently opened streams get a bounded number of retries", (t) => {
  useFakeSockets(t);
  t.mock.timers.enable({ apis: ["setTimeout"] });
  FakeSocket.created = [];
  openStream();
  // First open: the watchdog drops a connection that never seeded a frame.
  let socket = FakeSocket.created[0]!;
  socket.onopen!();
  socket.onmessage!({ data: "ignored text" });
  t.mock.timers.tick(firstFrameTimeoutMs);
  assert.equal(socket.closeCount, 1);
  socket.onclose!({ reason: "" });
  t.mock.timers.tick(reconnectBackoffMs);
  // Second open: still silent, still within the bounded budget.
  socket = FakeSocket.created[1]!;
  socket.onopen!();
  t.mock.timers.tick(firstFrameTimeoutMs);
  assert.equal(socket.closeCount, 1);
  socket.onclose!({ reason: "" });
  t.mock.timers.tick(reconnectBackoffMs);
  // Third open: retries are exhausted, the connection stays open.
  socket = FakeSocket.created[2]!;
  socket.onopen!();
  t.mock.timers.tick(firstFrameTimeoutMs * 10);
  assert.equal(socket.closeCount, 0);
});

void test("received frames restore the retry budget and stop the watchdog", (t) => {
  useFakeSockets(t);
  t.mock.timers.enable({ apis: ["setTimeout"] });
  FakeSocket.created = [];
  openStream();
  const socket = FakeSocket.created[0]!;
  socket.onopen!();
  socket.onmessage!({ data: new Blob([new Uint8Array([1])]) });
  t.mock.timers.tick(firstFrameTimeoutMs * 10);
  assert.equal(socket.closeCount, 0);
  // The stream drops, and the next silent open retries again.
  socket.onclose!({ reason: "Lost." });
  t.mock.timers.tick(reconnectBackoffMs);
  const reconnected = FakeSocket.created[1]!;
  reconnected.onopen!();
  t.mock.timers.tick(firstFrameTimeoutMs);
  assert.equal(reconnected.closeCount, 1);
});

void test("connections that close reconnect with backoff until closed", (t) => {
  useFakeSockets(t);
  t.mock.timers.enable({ apis: ["setTimeout"] });
  FakeSocket.created = [];
  const { stream, recording } = openStream();
  let socket = FakeSocket.created[0]!;
  socket.onopen!();
  socket.onclose!({ reason: "Lost." });
  assert.deepEqual(recording.states[2], ["closed", "Lost."]);
  t.mock.timers.tick(reconnectBackoffMs);
  // The retries count as failures until a connection opens again.
  socket = FakeSocket.created[1]!;
  assert.equal(socket.url, "ws://urd.test/browser-view/session/page");
  socket.onclose!({ reason: "" });
  assert.deepEqual(recording.states[3], ["connecting", null]);
  assert.deepEqual(recording.states[4], ["closed", "Connection lost."]);
  t.mock.timers.tick(reconnectBackoffMs * 2 - 1);
  assert.equal(FakeSocket.created.length, 2);
  t.mock.timers.tick(1);
  assert.equal(FakeSocket.created[2]!.url, socket.url);
  // Owner-initiated close stops the retries.
  stream.close();
  t.mock.timers.tick(60_000);
  assert.equal(FakeSocket.created.length, 3);
});
