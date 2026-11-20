export const encoder = new TextEncoder();

export async function waitFor(predicate: () => boolean, timeout = 2_000): Promise<void> {
  const deadline = Date.now() + timeout;
  while (!predicate()) {
    if (Date.now() >= deadline) throw new Error("Timed out waiting for session event activity.");
    await new Promise((resolve) => setTimeout(resolve, 5));
  }
}

export async function flush(): Promise<void> {
  for (let index = 0; index < 8; index++)
    await new Promise<void>((resolve) => setImmediate(resolve));
}

export function deferred<T>(): { promise: Promise<T>; resolve(value: T): void } {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((complete) => {
    resolve = complete;
  });
  return { promise, resolve };
}
