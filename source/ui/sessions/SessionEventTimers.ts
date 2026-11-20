const heartbeatTimeout = 90_000;

export class SessionEventTimers {
  private retryTimer: ReturnType<typeof setTimeout> | null = null;
  private watchdogTimer: ReturnType<typeof setTimeout> | null = null;

  readWithWatchdog(
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

  waitToRetry(delay: number, signal: AbortSignal): Promise<void> {
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

  clearRetryTimer(): void {
    if (this.retryTimer === null) return;
    clearTimeout(this.retryTimer);
    this.retryTimer = null;
  }

  clearWatchdogTimer(): void {
    if (this.watchdogTimer === null) return;
    clearTimeout(this.watchdogTimer);
    this.watchdogTimer = null;
  }
}

export class StreamTimeoutError extends Error {
  constructor() {
    super("Session event stream timed out without a heartbeat.");
    this.name = "StreamTimeoutError";
  }
}
