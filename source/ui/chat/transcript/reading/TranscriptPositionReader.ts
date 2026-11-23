import type {
  TranscriptEntryRef,
  TranscriptPositionWindow,
} from "../../../sessions/transcript/Messages.ts";
import type { SessionClient } from "../../../sessions/SessionClient.ts";

export type TranscriptPositionTarget =
  | { readonly kind: "entry"; readonly entry: TranscriptEntryRef }
  | { readonly kind: "fraction"; readonly historyRevision: number; readonly fraction: number };

// Owns the current position-window read for one mounted transcript.
type TranscriptPositionClient = Pick<SessionClient, "getEntryPosition" | "locatePosition">;

interface PositionRead {
  readonly result: Promise<TranscriptPositionWindow | null>;
  readonly historyRevision: number;
  readonly notice: string | null;
}

export class TranscriptPositionReader {
  private activeRequest: AbortController | null = null;
  private latestRead: PositionRead | null = null;
  private requestGeneration = 0;
  private readonly client: TranscriptPositionClient;
  private readonly sessionId: string;

  constructor(client: TranscriptPositionClient, sessionId: string) {
    this.client = client;
    this.sessionId = sessionId;
  }

  // Let a read finish before revisiting a newer notice for the same history.
  shouldRefresh(historyRevision: number, notice: string | null): boolean {
    return (
      this.latestRead?.historyRevision !== historyRevision ||
      (this.activeRequest === null && notice !== null && this.latestRead.notice !== notice)
    );
  }

  read(
    target: TranscriptPositionTarget,
    notice: string | null = null,
  ): Promise<TranscriptPositionWindow | null> {
    this.activeRequest?.abort();
    const controller = new AbortController();
    this.activeRequest = controller;
    const generation = ++this.requestGeneration;
    const result = this.readWindow(target, controller, generation);
    const historyRevision =
      target.kind === "entry" ? target.entry.historyRevision : target.historyRevision;
    this.latestRead = { result, historyRevision, notice };
    return result;
  }

  // Recheck at publication: another request may start after this promise settles.
  isCurrent(read: Promise<TranscriptPositionWindow | null>): boolean {
    return this.latestRead?.result === read;
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
