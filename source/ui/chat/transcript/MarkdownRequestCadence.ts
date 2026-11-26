// Limit repeated streaming generations without delaying the first or closed extent.
export class MarkdownRequestCadence {
  private lastStarted: number | null = null;

  delay(now: number, closed: boolean): number {
    if (closed || this.lastStarted === null) return 0;
    return Math.max(0, 100 - (now - this.lastStarted));
  }

  started(now: number): void {
    this.lastStarted = now;
  }

  reset(): void {
    this.lastStarted = null;
  }
}
