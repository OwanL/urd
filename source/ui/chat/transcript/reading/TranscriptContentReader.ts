interface PendingRead {
  readonly signal: AbortSignal;
  readonly operation: () => Promise<unknown>;
  readonly resolve: (value: unknown) => void;
  readonly reject: (reason?: unknown) => void;
  onAbort: (() => void) | null;
}

interface SessionQueue {
  active: boolean;
  waiting: PendingRead[];
}

export class TranscriptContentReader {
  private readonly sessions = new Map<string, SessionQueue>();

  read<T>(sessionId: string, signal: AbortSignal, operation: () => Promise<T>): Promise<T> {
    if (signal.aborted) return Promise.reject(signal.reason);

    return new Promise<T>((resolve, reject) => {
      let queue = this.sessions.get(sessionId);
      if (!queue) {
        queue = { active: false, waiting: [] };
        this.sessions.set(sessionId, queue);
      }

      const request: PendingRead = {
        signal,
        operation,
        resolve: (value) => resolve(value as T),
        reject,
        onAbort: null,
      };
      const onAbort = () => {
        const index = queue.waiting.indexOf(request);
        if (index < 0) return;
        queue.waiting.splice(index, 1);
        signal.removeEventListener("abort", onAbort);
        reject(signal.reason);
        this.dispatchNext(sessionId, queue);
      };
      request.onAbort = onAbort;
      queue.waiting.push(request);
      signal.addEventListener("abort", onAbort, { once: true });
      if (signal.aborted) onAbort();
      this.dispatchNext(sessionId, queue);
    });
  }

  private dispatchNext(sessionId: string, queue: SessionQueue): void {
    if (queue.active) return;

    const request = queue.waiting.shift();
    if (!request) {
      if (this.sessions.get(sessionId) === queue) this.sessions.delete(sessionId);
      return;
    }

    if (request.onAbort) {
      request.signal.removeEventListener("abort", request.onAbort);
      request.onAbort = null;
    }
    if (request.signal.aborted) {
      request.reject(request.signal.reason);
      this.dispatchNext(sessionId, queue);
      return;
    }

    queue.active = true;
    let result: Promise<unknown>;
    try {
      result = Promise.resolve(request.operation());
    } catch (cause) {
      result = Promise.reject(cause);
    }
    void result.then(
      (value) => {
        request.resolve(value);
        queue.active = false;
        this.dispatchNext(sessionId, queue);
      },
      (cause: unknown) => {
        request.reject(cause);
        queue.active = false;
        this.dispatchNext(sessionId, queue);
      },
    );
  }
}
