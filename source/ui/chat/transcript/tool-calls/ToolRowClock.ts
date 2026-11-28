const subscribers = new Set<(now: number) => void>();
let timer: number | null = null;

export function subscribeToToolRowClock(update: (now: number) => void): () => void {
  subscribers.add(update);
  timer ??= window.setInterval(() => {
    const now = Date.now();
    for (const subscriber of subscribers) subscriber(now);
  }, 1_000);
  return () => {
    subscribers.delete(update);
    if (subscribers.size === 0 && timer !== null) {
      window.clearInterval(timer);
      timer = null;
    }
  };
}
