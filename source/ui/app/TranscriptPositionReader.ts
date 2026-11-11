import type { TranscriptEntryRef, TranscriptPositionWindow } from "../sessions/Messages.ts";
import type { SessionClient } from "../sessions/SessionClient.ts";

export type TranscriptPositionTarget =
  | { readonly kind: "entry"; readonly entry: TranscriptEntryRef }
  | { readonly kind: "fraction"; readonly historyRevision: number; readonly fraction: number };

// Owns the current position-window read for one mounted transcript.
type TranscriptPositionClient = Pick<SessionClient, "getEntryPosition" | "locatePosition">;

export class TranscriptPositionReader {
  private activeRequest: AbortController | null = null;
  private latestRead: Promise<TranscriptPositionWindow | null> | null = null;
  private requestGeneration = 0;
  private readonly client: TranscriptPositionClient;
  private readonly sessionId: string;

  constructor(client: TranscriptPositionClient, sessionId: string) {
    this.client = client;
    this.sessionId = sessionId;
  }

  read(target: TranscriptPositionTarget): Promise<TranscriptPositionWindow | null> {
    this.activeRequest?.abort();
    const controller = new AbortController();
    this.activeRequest = controller;
    const generation = ++this.requestGeneration;
    const read = this.readWindow(target, controller, generation);
    this.latestRead = read;
    return read;
  }

  // Recheck at publication: another request may start after this promise settles.
  isCurrent(read: Promise<TranscriptPositionWindow | null>): boolean {
    return this.latestRead === read;
  }

  cancel(): void {
    this.requestGeneration++;
    this.latestRead = null;
    this.activeRequest?.abort();
    this.activeRequest = null;
  }

  private async readWindow(
    target: TranscriptPositionTarget,
    controller: AbortController,
    generation: number,
  ): Promise<TranscriptPositionWindow | null> {
    try {
      const window =
        target.kind === "entry"
          ? await this.client.getEntryPosition(this.sessionId, target.entry, controller.signal)
          : await this.client.locatePosition(
              this.sessionId,
              target.historyRevision,
              target.fraction,
              controller.signal,
            );
      return this.isCurrentRequest(controller, generation) ? window : null;
    } catch (cause) {
      if (!this.isCurrentRequest(controller, generation)) return null;
      throw cause;
    } finally {
      if (this.activeRequest === controller) this.activeRequest = null;
    }
  }

  private isCurrentRequest(controller: AbortController, generation: number): boolean {
    return (
      !controller.signal.aborted &&
      this.activeRequest === controller &&
      this.requestGeneration === generation
    );
  }
}
