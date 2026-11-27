import { SessionEventTimers, StreamTimeoutError } from "./SessionEventTimers.ts";
import { postSessionEventRegistration } from "./SessionEventRegistration.ts";
import { EventStreamParser, type SessionEventMessage } from "./EventStreamParser.ts";

export interface ToolDraft {
  session: string;
  boot: string;
  version: number;
  executionId: string;
  requestId: string;
  callId: string;
  entryId: string;
  name: string;
  order: number;
  state: "drafting" | "drafted" | "promoted" | "discarded";
  argumentBytes: number;
  field?: string | null;
  head: string;
  tail: string;
}

export type AttentionKind = "question" | "completion";

export interface SessionEventCallbacks {
  connected(boot: string, streamId: string): void;
  change(session: string, notice: string): void;
  status(
    session: string,
    status: string,
    title: string | null,
    hasPendingUserQuestion: boolean,
  ): void;
  created(session: string): void;
  attention?(kind: AttentionKind): void;
  browser?(session: string): void;
  toolDraft?(draft: ToolDraft): void;
  disconnected(): void;
  error?(message: string): void;
  provisioning?(stage: BrowserProvisioningStage, detail: string | null): void;
  snoozeChanged?(): void;
}

// Host-wide browser provisioning stages; detail carries the installer's report while downloading.
export type BrowserProvisioningStage = "downloading" | "ready" | "failed";
const provisioningStages = new Set<BrowserProvisioningStage>(["downloading", "ready", "failed"]);

interface RegistrationHandshake {
  readonly generation: number;
  readonly streamId: string;
  readonly number: number;
  readonly watched: Set<string>;
  readonly statuses: Set<string>;
  readonly pendingChanges: Map<string, string>;
  acknowledged: boolean;
}

const registrationTimeout = 15_000;
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
  private readonly timers = new SessionEventTimers();
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
    this.timers.clearRetryTimer();
    this.timers.clearWatchdogTimer();
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
      let aborted = false;

      try {
        await this.openStream(controller, generation);
        failure = new Error("Session event stream ended.");
      } catch (error) {
        failure = error;
      } finally {
        // Cleanup aborts the controller too; preserve whether the failure preceded it.
        aborted = controller.signal.aborted;
        this.clearConnection(controller);
      }

      if (!this.isCurrent(generation)) return;
      if (!aborted || failure instanceof StreamTimeoutError)
        this.reportError(errorMessage(failure));

      await this.timers.waitToRetry(retryDelay, lifecycleSignal);
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
        const next = await this.timers.readWithWatchdog(reader, controller);
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

    if (!this.connected) return;

    if (message.event === "attention") {
      const { kind } = event;
      if (kind === "question" || kind === "completion")
        this.call(() => this.callbacks.attention?.(kind));
      return;
    }

    // Browser provisioning is a host-wide notice; it carries no session identity.
    if (message.event === "browser-provisioning") {
      if (
        typeof event.state !== "string" ||
        !provisioningStages.has(event.state as BrowserProvisioningStage)
      )
        return;
      const detail = typeof event.detail === "string" ? event.detail : null;
      this.call(() =>
        this.callbacks.provisioning?.(event.state as BrowserProvisioningStage, detail),
      );
      return;
    }

    if (message.event === "snooze-changed") {
      this.call(() => this.callbacks.snoozeChanged?.());
      return;
    }

    if (typeof event.session !== "string" || !event.session) return;

    if (message.event === "session-created") {
      this.call(() => this.callbacks.created(event.session as string));
      return;
    }

    // Browser page metadata for one session changed; viewers re-read the page list.
    if (message.event === "browser") {
      this.call(() => this.callbacks.browser?.(event.session as string));
      return;
    }

    if (
      typeof event.boot !== "string" ||
      typeof event.version !== "number" ||
      !Number.isFinite(event.version)
    )
      return;

    if (message.event === "tool-draft") {
      const registration = this.registration;
      if (
        registration === null ||
        registration.generation !== generation ||
        registration.streamId !== this.streamId ||
        !registration.watched.has(event.session) ||
        !this.desiredWatched.includes(event.session) ||
        !isToolDraft(event)
      )
        return;
      this.call(() => this.callbacks.toolDraft?.(event));
      return;
    }

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
      const title = typeof event.title === "string" && event.title ? event.title : null;
      this.call(() =>
        this.callbacks.status(
          event.session as string,
          event.status as string,
          title,
          event.hasPendingUserQuestion === true,
        ),
      );
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
        const timeout = setTimeout(
          () => controller.abort(new Error("Session event registration timed out.")),
          registrationTimeout,
        );

        try {
          await postSessionEventRegistration(
            this.origin,
            streamId,
            open,
            watched,
            controller.signal,
          );
        } catch (error) {
          if (this.isCurrent(generation) && this.updateController === controller) {
            this.reportError(errorMessage(error));
            this.disconnectStream();
          }
          return;
        } finally {
          clearTimeout(timeout);
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
    this.timers.clearWatchdogTimer();
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

function isToolDraft(event: Record<string, unknown>): event is Record<string, unknown> & ToolDraft {
  return (
    ["session", "boot", "executionId", "requestId", "callId", "entryId", "name"].every(
      (key) => typeof event[key] === "string" && event[key] !== "",
    ) &&
    ["head", "tail"].every((key) => typeof event[key] === "string") &&
    (event.field == null || typeof event.field === "string") &&
    ["version", "order", "argumentBytes"].every(
      (key) => Number.isSafeInteger(event[key]) && (event[key] as number) >= 0,
    ) &&
    ["drafting", "drafted", "promoted", "discarded"].includes(event.state as string)
  );
}

function sameSet(left: readonly string[], right: readonly string[]): boolean {
  if (left.length !== right.length) return false;
  const values = new Set(left);
  return right.every((value) => values.has(value));
}

function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}
