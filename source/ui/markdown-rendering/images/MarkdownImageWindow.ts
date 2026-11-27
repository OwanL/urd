import {
  isMarkdownImageKey,
  type MarkdownImageLoader,
  type MarkdownRenderSnapshot,
} from "../MarkdownRenderModel.ts";

type ImageState =
  | { kind: "pending" }
  | { kind: "ready"; url: string; width: number; height: number }
  | { kind: "unavailable" };

// The importer rechecks the full source once when a response closes, so a local image
// that failed while streaming can resolve shortly after the closed extent is published.
const closedRetryLimit = 4;
const closedRetryDelayMs = 250;

// Only the mounted message window owns URLs. Projection IDs are generation-local, so an
// unchanged occurrence retains its resource by message, source range and image key instead.
export class MarkdownImageWindow {
  private retained = new Map<string, MarkdownImageResource>();
  private readonly changed: () => void;

  constructor(changed: () => void) {
    this.changed = changed;
  }

  update(
    snapshot: MarkdownRenderSnapshot,
    loader: MarkdownImageLoader | null,
    closed: boolean,
  ): ReadonlyMap<number, MarkdownImageResource> {
    const resources = new Map<number, MarkdownImageResource>();
    const retained = new Map<string, MarkdownImageResource>();
    if (loader) {
      for (const { context, values } of snapshot.contexts.values()) {
        const key = values.imageKey;
        if (context.kind !== "image" || !isMarkdownImageKey(key)) continue;
        const identity = `${loader.scope}:${context.source.start}:${context.source.end}:${key}`;
        const resource =
          retained.get(identity) ??
          this.retained.get(identity) ??
          new MarkdownImageResource(loader, key, this.changed);
        retained.set(identity, resource);
        resources.set(context.id, resource);
      }
    }
    for (const [identity, resource] of this.retained)
      if (!retained.has(identity)) resource.dispose();
    this.retained = retained;
    if (closed) {
      for (const resource of resources.values()) resource.retryWhenResponseClosed();
      return resources;
    }
    return resources;
  }

  dispose(): void {
    for (const resource of this.retained.values()) resource.dispose();
    this.retained.clear();
  }
}

export class MarkdownImageResource {
  private current: ImageState = { kind: "pending" };
  private controller: AbortController | null = null;
  private disposed = false;
  private responseClosed = false;
  private closedRetryAttempts = 0;
  private closedRetryTimer: ReturnType<typeof setTimeout> | undefined;
  private readonly loader: MarkdownImageLoader;
  private readonly key: string;
  private readonly changed: () => void;

  constructor(loader: MarkdownImageLoader, key: string, changed: () => void) {
    this.loader = loader;
    this.key = key;
    this.changed = changed;
  }

  get state(): ImageState {
    return this.current;
  }

  async load(): Promise<void> {
    if (this.disposed || this.controller || this.current.kind !== "pending") return;
    const controller = new AbortController();
    this.controller = controller;
    let url: string | undefined;
    let state: ImageState;
    try {
      const blob = await this.loader.load(this.key, controller.signal);
      controller.signal.throwIfAborted();
      url = URL.createObjectURL(blob);
      const size = await decodeImage(url, controller.signal);
      controller.signal.throwIfAborted();
      state = { kind: "ready", url, ...size };
    } catch {
      if (url) URL.revokeObjectURL(url);
      state = { kind: "unavailable" };
    } finally {
      this.controller = null;
    }
    if (controller.signal.aborted) return;
    this.publish(state);
    if (
      state.kind === "unavailable" &&
      this.responseClosed &&
      this.closedRetryAttempts < closedRetryLimit
    )
      this.scheduleClosedRetry();
  }

  // A closed publication gives unavailable images a bounded retry; loaded images are
  // kept. An in-flight visible load also reaches the schedule through its own failure.
  retryWhenResponseClosed(): void {
    if (this.disposed || this.responseClosed) return;
    this.responseClosed = true;
    if (this.current.kind === "unavailable") this.beginClosedRetry();
  }

  imageFailed(url: string): void {
    if (this.disposed || this.current.kind !== "ready" || this.current.url !== url) return;
    URL.revokeObjectURL(url);
    this.publish({ kind: "unavailable" });
  }

  dispose(): void {
    this.disposed = true;
    clearTimeout(this.closedRetryTimer);
    this.controller?.abort();
    if (this.current.kind === "ready") URL.revokeObjectURL(this.current.url);
  }

  private publish(state: ImageState): void {
    this.current = state;
    this.changed();
  }

  private scheduleClosedRetry(): void {
    if (this.disposed || this.closedRetryTimer !== undefined) return;
    this.closedRetryTimer = setTimeout(() => {
      this.closedRetryTimer = undefined;
      this.beginClosedRetry();
    }, closedRetryDelayMs);
  }

  private beginClosedRetry(): void {
    if (this.disposed || this.controller) return;
    this.closedRetryAttempts++;
    // The intermediate reset is not announced; each attempt publishes its own outcome.
    this.current = { kind: "pending" };
    void this.load();
  }
}

// Decode before altering the transcript, so the replacement has its final geometry.
async function decodeImage(url: string, signal: AbortSignal) {
  const image = new Image();
  const cancel = () => image.removeAttribute("src");
  signal.addEventListener("abort", cancel, { once: true });
  try {
    image.src = url;
    await image.decode();
    if (!image.naturalWidth || !image.naturalHeight) throw new Error("Image has no dimensions.");
    return { width: image.naturalWidth, height: image.naturalHeight };
  } finally {
    signal.removeEventListener("abort", cancel);
  }
}
