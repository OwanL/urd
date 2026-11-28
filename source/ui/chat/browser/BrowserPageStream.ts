import type { BrowserPageCommand } from "./BrowserPage.ts";

export type BrowserPageStreamState = "connecting" | "open" | "closed";

export interface BrowserPageStreamHandlers {
  frame(frame: Blob): void;
  stateChange(state: BrowserPageStreamState, failure: string | null): void;
}

// The host seeds a newly connected stream with its first frame; an open
// connection that stays silent gets a bounded number of retries.
const firstFrameTimeoutMs = 3000;
const firstFrameRetryLimit = 2;

// One page's live JPEG stream on the session's host; reconnects with backoff
// until its owner closes it.
export class BrowserPageStream {
  private readonly sessionId: string;
  private readonly pageId: string;
  private readonly handlers: BrowserPageStreamHandlers;
  private socket: WebSocket | null = null;
  private retryTimer: ReturnType<typeof setTimeout> | undefined;
  private firstFrameTimer: ReturnType<typeof setTimeout> | undefined;
  private failures = 0;
  private silentOpens = 0;
  private framesSinceOpen = 0;
  private closed = false;

  constructor(sessionId: string, pageId: string, handlers: BrowserPageStreamHandlers) {
    this.sessionId = sessionId;
    this.pageId = pageId;
    this.handlers = handlers;
  }

  open(): void {
    if (this.closed || this.socket) return;
    this.connect();
  }

  close(): void {
    this.closed = true;
    clearTimeout(this.retryTimer);
    this.retryTimer = undefined;
    this.clearFirstFrameTimer();
    this.socket?.close();
    this.socket = null;
  }

  send(command: BrowserPageCommand): boolean {
    const socket = this.socket;
    if (!socket || socket.readyState !== WebSocket.OPEN) return false;
    socket.send(JSON.stringify(command));
    return true;
  }

  private connect(): void {
    const socket = new WebSocket(
      `ws${location.protocol === "https:" ? "s" : ""}://${location.host}/browser-view/` +
        `${encodeURIComponent(this.sessionId)}/${encodeURIComponent(this.pageId)}`,
    );
    socket.binaryType = "blob";
    this.socket = socket;
    this.framesSinceOpen = 0;
    this.handlers.stateChange("connecting", null);
    socket.onopen = () => {
      if (socket !== this.socket) return;
      this.failures = 0;
      this.watchForFirstFrame();
      this.handlers.stateChange("open", null);
    };
    socket.onmessage = (event) => {
      // The protocol only replies with JPEG frames; unexpected text is ignored.
      if (socket !== this.socket || typeof event.data === "string") return;
      this.framesSinceOpen += 1;
      this.silentOpens = 0;
      this.clearFirstFrameTimer();
      this.handlers.frame(event.data as Blob);
    };
    socket.onclose = (event) => {
      if (socket !== this.socket) return;
      this.socket = null;
      this.clearFirstFrameTimer();
      if (this.closed) return;
      this.failures += 1;
      this.handlers.stateChange("closed", event.reason || "Connection lost.");
      const delay = Math.min(500 * 2 ** (this.failures - 1), 5000);
      this.retryTimer = setTimeout(() => {
        this.retryTimer = undefined;
        this.connect();
      }, delay);
    };
  }

  // A seeded stream should show its first frame quickly; an open connection
  // that stays silent is dropped so the usual backoff can reconnect it.
  private watchForFirstFrame(): void {
    if (this.silentOpens >= firstFrameRetryLimit) return;
    this.firstFrameTimer = setTimeout(() => {
      this.firstFrameTimer = undefined;
      if (this.framesSinceOpen > 0) return;
      this.silentOpens += 1;
      this.socket?.close();
    }, firstFrameTimeoutMs);
  }

  private clearFirstFrameTimer(): void {
    clearTimeout(this.firstFrameTimer);
    this.firstFrameTimer = undefined;
  }
}
