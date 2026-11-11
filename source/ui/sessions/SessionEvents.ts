export interface SessionEventCallbacks {
  connected(boot: string, streamId: string): void;
  change(session: string, notice: string): void;
  status(session: string, status: string): void;
  created(session: string): void;
  disconnected(): void;
  error?(message: string): void;
}

interface SessionEventMessage {
  readonly event: string;
  readonly data: string;
}

interface RegistrationHandshake {
  readonly generation: number;
  readonly streamId: string;
  readonly number: number;
  readonly watched: Set<string>;
  readonly statuses: Set<string>;
  readonly pendingChanges: Map<string, string>;
  acknowledged: boolean;
}

const maxFrameLength = 64 * 1024;
const heartbeatTimeout = 90_000;
const initialRetryDelay = 250;
const maxRetryDelay = 5_000;

// Owns one page's event stream and the latest open/watch registration.
export class SessionEvents {
  private readonly origin: string;
  private readonly callbacks: SessionEventCallbacks;
  private running = false;
  private generation = 0;
  private lifecycle: AbortController | null = null;
  private streamController: AbortController | null = null;
  private activeReader: ReadableStreamDefaultReader<Uint8Array> | null = null;
  private updateController: AbortController | null = null;
  private retryTimer: ReturnType<typeof setTimeout> | null = null;
  private watchdogTimer: ReturnType<typeof setTimeout> | null = null;
  private streamId: string | null = null;
  private connected = false;
  private connectionNumber = 0;
  private registrationNumber = 0;
  private desiredOpen: string[] = [];
  private desiredWatched: string[] = [];
  private registration: RegistrationHandshake | null = null;
  private desiredRevision = 0;
  private updatePending = false;
  private updateWorker: symbol | null = null;

  constructor(origin: URL, callbacks: SessionEventCallbacks) {
    this.origin = origin.origin;
    this.callbacks = callbacks;
  }

  start(): void {
    if (this.running) return;

    this.running = true;
    const generation = ++this.generation;
    const lifecycle = new AbortController();
    this.lifecycle = lifecycle;
    this.registration = null;
    void this.run(generation, lifecycle.signal);
  }

  stop(): void {
    if (!this.running) return;

    this.running = false;
    this.generation++;
    this.lifecycle?.abort();
    this.lifecycle = null;
    this.clearRetryTimer();
    this.clearWatchdogTimer();
    this.updateController?.abort();
    this.updateController = null;
    this.updateWorker = null;
    this.clearConnection(this.streamController);
    this.registration = null;
    this.updatePending = false;
  }

  update(open: readonly string[], watched: readonly string[]): void {
    const nextOpen = [...new Set(open)];
    const openSet = new Set(nextOpen);
    const nextWatched = [...new Set(watched)].filter((session) => openSet.has(session));
    if (sameSet(this.desiredOpen, nextOpen) && sameSet(this.desiredWatched, nextWatched)) return;

    this.desiredOpen = nextOpen;
    this.desiredWatched = nextWatched;
    this.desiredRevision++;
    if (this.running && this.streamId !== null) {
      this.updatePending = true;
      void this.sendUpdates(this.generation);
    }
  }

  private async run(generation: number, lifecycleSignal: AbortSignal): Promise<void> {
    let retryDelay = initialRetryDelay;

    while (this.isCurrent(generation)) {
      const controller = new AbortController();
      this.streamController = controller;
      let failure: unknown;

      try {
        await this.openStream(controller, generation);
        failure = new Error("Session event stream ended.");
      } catch (error) {
        failure = error;
      } finally {
        this.clearConnection(controller);
      }

      if (!this.isCurrent(generation)) return;
      if (!controller.signal.aborted || failure instanceof StreamTimeoutError)
        this.reportError(errorMessage(failure));

      await this.waitToRetry(retryDelay, lifecycleSignal);
      retryDelay = Math.min(retryDelay * 2, maxRetryDelay);
    }
  }

  private async openStream(controller: AbortController, generation: number): Promise<void> {
    const response = await fetch(new URL("/api/events", this.origin), {
      method: "GET",
      headers: {
        "X-Urd-Client": "browser",
        Accept: "text/event-stream",
      },
      cache: "no-store",
      credentials: "omit",
      redirect: "error",
      signal: controller.signal,
    });

    if (!response.ok) throw new Error(`Session event stream failed (HTTP ${response.status}).`);
    if (response.body === null) throw new Error("Session event stream returned no body.");
    const mediaType = response.headers.get("content-type")?.split(";")[0]?.trim().toLowerCase();
    if (mediaType !== "text/event-stream")
      throw new Error("Server returned a non-SSE event stream.");

    const reader = response.body.getReader();
    this.activeReader = reader;
    const decoder = new TextDecoder();
    const parser = new EventStreamParser((message) => this.handleMessage(message, generation));

    try {
      while (this.isCurrent(generation)) {
        const next = await this.readWithWatchdog(reader, controller);
        if (next.done) break;
        const text = decoder.decode(next.value, { stream: true });
        parser.push(text);
      }
      parser.push(decoder.decode());
    } finally {
      if (this.activeReader === reader) this.activeReader = null;
      reader.releaseLock();
    }
  }

  private handleMessage(message: SessionEventMessage, generation: number): void {
    if (!this.isCurrent(generation)) return;

    let data: unknown;
    try {
      data = JSON.parse(message.data) as unknown;
    } catch {
      this.reportError("Server sent an invalid session event.");
      return;
    }
    if (typeof data !== "object" || data === null) return;
    const event = data as Record<string, unknown>;

    if (message.event === "connected") {
      if (
        this.connected ||
        typeof event.stream !== "string" ||
        !event.stream ||
        typeof event.boot !== "string" ||
        !event.boot
      )
        return;

      this.streamId = event.stream;
      this.connected = true;
      this.connectionNumber++;
      this.registration = null;
      this.updatePending = true;
      this.call(() => this.callbacks.connected(event.boot as string, event.stream as string));
      if (this.isCurrent(generation)) void this.sendUpdates(generation);
      return;
    }

    if (!this.connected || typeof event.session !== "string" || !event.session) return;
    if (message.event === "session-created") {
      this.call(() => this.callbacks.created(event.session as string));
      return;
    }

    if (
      typeof event.boot !== "string" ||
      typeof event.version !== "number" ||
      !Number.isFinite(event.version)
    )
      return;

    if (message.event === "change") {
      const registration = this.registration;
      if (
        registration === null ||
        registration.generation !== generation ||
        registration.streamId !== this.streamId ||
        !registration.watched.has(event.session) ||
        !this.desiredWatched.includes(event.session)
      )
        return;
      const notice = `${this.connectionNumber}:${event.boot}:${event.version}:${registration.number}`;
      if (!registration.acknowledged || !registration.statuses.has(event.session)) {
        registration.pendingChanges.set(event.session, notice);
        return;
      }
      this.call(() => this.callbacks.change(event.session as string, notice));
      return;
    }

    if (message.event === "status" && typeof event.status === "string") {
      const registration = this.registration;
      if (
        registration !== null &&
        registration.generation === generation &&
        registration.streamId === this.streamId &&
        registration.watched.has(event.session) &&
        this.desiredWatched.includes(event.session)
      )
        registration.statuses.add(event.session);
      if (!this.desiredOpen.includes(event.session)) return;
      this.call(() => this.callbacks.status(event.session as string, event.status as string));
      if (registration !== null) this.releasePendingChange(registration, event.session);
    }
  }

  private async sendUpdates(generation: number): Promise<void> {
    if (this.updateWorker !== null || !this.isCurrent(generation) || this.streamId === null) return;
    const worker = Symbol();
    this.updateWorker = worker;

    try {
      while (this.updatePending && this.isCurrent(generation) && this.streamId !== null) {
        this.updatePending = false;
        const streamId: string = this.streamId;
        const revision = this.desiredRevision;
        const open = [...this.desiredOpen];
        const watched = [...this.desiredWatched];
        const controller = new AbortController();
        this.updateController = controller;
        const registration: RegistrationHandshake = {
          generation,
          streamId,
          number: ++this.registrationNumber,
          watched: new Set(watched),
          statuses: new Set(),
          pendingChanges: new Map(),
          acknowledged: false,
        };
        this.registration = registration;

        try {
          const response = await fetch(
            new URL(`/api/events/${encodeURIComponent(streamId)}`, this.origin),
            {
              method: "POST",
              headers: {
                "X-Urd-Client": "browser",
                "Content-Type": "application/json",
              },
              body: JSON.stringify({ open, watched }),
              cache: "no-store",
              credentials: "omit",
              redirect: "error",
              signal: controller.signal,
            },
          );
          if (response.status !== 204)
            throw new Error(`Session event registration failed (HTTP ${response.status}).`);
        } catch (error) {
          if (this.isCurrent(generation) && !controller.signal.aborted) {
            this.reportError(errorMessage(error));
            this.updatePending = false;
            this.disconnectStream();
          }
          return;
        } finally {
          if (this.updateController === controller) this.updateController = null;
        }

        if (!this.isCurrent(generation) || this.streamId !== streamId) return;
        this.acknowledgeRegistration(registration, generation);
        if (revision !== this.desiredRevision) this.updatePending = true;
      }
    } finally {
      if (this.updateWorker === worker) this.updateWorker = null;
      if (
        this.updateWorker === null &&
        this.updatePending &&
        this.running &&
        this.streamId !== null
      )
        void this.sendUpdates(this.generation);
    }
  }

  private acknowledgeRegistration(registration: RegistrationHandshake, generation: number): void {
    if (!this.isCurrent(generation) || this.registration !== registration) return;
    registration.acknowledged = true;
    for (const session of [...registration.pendingChanges.keys()]) {
      if (!this.isCurrent(generation) || this.registration !== registration) break;
      this.releasePendingChange(registration, session);
    }
  }

  private releasePendingChange(registration: RegistrationHandshake, session: string): void {
    if (
      !this.isCurrent(registration.generation) ||
      this.registration !== registration ||
      this.streamId !== registration.streamId ||
      !registration.acknowledged ||
      !registration.statuses.has(session) ||
      !registration.watched.has(session) ||
      !this.desiredWatched.includes(session)
    )
      return;

    const notice = registration.pendingChanges.get(session);
    if (notice === undefined) return;
    registration.pendingChanges.delete(session);
    this.call(() => this.callbacks.change(session, notice));
  }

  private disconnectStream(): void {
    const controller = this.streamController;
    if (controller === null) return;
    this.clearConnection(controller);
  }

  private clearConnection(controller: AbortController | null): void {
    if (controller === null || this.streamController !== controller) return;

    this.streamController = null;
    this.streamId = null;
    this.updatePending = false;
    this.registration = null;
    this.clearWatchdogTimer();
    if (this.updateController !== null) {
      this.updateController.abort();
      this.updateController = null;
    }
    this.updateWorker = null;
    controller.abort();

    const reader = this.activeReader;
    this.activeReader = null;
    if (reader !== null) void reader.cancel().catch(() => undefined);

    if (this.connected) {
      this.connected = false;
      this.call(() => this.callbacks.disconnected());
    }
  }

  private readWithWatchdog(
    reader: ReadableStreamDefaultReader<Uint8Array>,
    controller: AbortController,
  ): Promise<ReadableStreamReadResult<Uint8Array>> {
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        if (this.watchdogTimer === timer) this.watchdogTimer = null;
        controller.abort();
        reject(new StreamTimeoutError());
      }, heartbeatTimeout);
      this.watchdogTimer = timer;

      reader.read().then(
        (result) => {
          if (this.watchdogTimer === timer) this.watchdogTimer = null;
          clearTimeout(timer);
          resolve(result);
        },
        (error: unknown) => {
          if (this.watchdogTimer === timer) this.watchdogTimer = null;
          clearTimeout(timer);
          reject(error);
        },
      );
    });
  }

  private waitToRetry(delay: number, signal: AbortSignal): Promise<void> {
    if (signal.aborted) return Promise.resolve();

    return new Promise((resolve) => {
      const finish = () => {
        this.clearRetryTimer();
        signal.removeEventListener("abort", finish);
        resolve();
      };
      this.retryTimer = setTimeout(finish, delay);
      signal.addEventListener("abort", finish, { once: true });
    });
  }

  private clearRetryTimer(): void {
    if (this.retryTimer === null) return;
    clearTimeout(this.retryTimer);
    this.retryTimer = null;
  }

  private clearWatchdogTimer(): void {
    if (this.watchdogTimer === null) return;
    clearTimeout(this.watchdogTimer);
    this.watchdogTimer = null;
  }

  private isCurrent(generation: number): boolean {
    return this.running && this.generation === generation;
  }

  private call(callback: () => void): void {
    try {
      callback();
    } catch (error) {
      this.reportError(errorMessage(error));
    }
  }

  private reportError(message: string): void {
    try {
      this.callbacks.error?.(message);
    } catch {
      // Consumer callbacks must not interrupt transport parsing or cleanup.
    }
  }
}

class EventStreamParser {
  private readonly dispatch: (message: SessionEventMessage) => void;
  private lineParts: string[] = [];
  private lineLength = 0;
  private frameLength = 0;
  private eventName = "";
  private dataLines: string[] = [];
  private firstLine = true;

  constructor(dispatch: (message: SessionEventMessage) => void) {
    this.dispatch = dispatch;
  }

  push(text: string): void {
    let start = 0;
    while (start < text.length) {
      const newline = text.indexOf("\n", start);
      const end = newline === -1 ? text.length : newline;
      const piece = text.slice(start, end);
      const nextLength =
        this.frameLength + this.lineLength + piece.length + (newline === -1 ? 0 : 1);
      if (nextLength > maxFrameLength) throw new Error("Session event frame exceeded 64 KB.");

      this.lineParts.push(piece);
      this.lineLength += piece.length;
      if (newline === -1) return;

      const rawLength = this.lineLength;
      let line = this.lineParts.join("");
      this.lineParts = [];
      this.lineLength = 0;
      if (line.endsWith("\r")) line = line.slice(0, -1);
      this.frameLength += rawLength + 1;
      if (line.length === 0) {
        this.finishFrame();
      } else {
        this.readLine(line);
      }
      start = newline + 1;
    }
  }

  private readLine(line: string): void {
    if (this.firstLine && line.startsWith("\uFEFF")) line = line.slice(1);
    this.firstLine = false;
    if (line.startsWith(":")) return;

    const colon = line.indexOf(":");
    const field = colon === -1 ? line : line.slice(0, colon);
    let value = colon === -1 ? "" : line.slice(colon + 1);
    if (value.startsWith(" ")) value = value.slice(1);
    if (field === "event") this.eventName = value;
    if (field === "data") this.dataLines.push(value);
  }

  private finishFrame(): void {
    const event = this.eventName || "message";
    const data = this.dataLines.join("\n");
    const hasData = this.dataLines.length > 0;
    this.eventName = "";
    this.dataLines = [];
    this.frameLength = 0;
    if (hasData) this.dispatch({ event, data });
  }
}

class StreamTimeoutError extends Error {
  constructor() {
    super("Session event stream timed out without a heartbeat.");
    this.name = "StreamTimeoutError";
  }
}

function sameSet(left: readonly string[], right: readonly string[]): boolean {
  if (left.length !== right.length) return false;
  const values = new Set(left);
  return right.every((value) => values.has(value));
}

function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}
