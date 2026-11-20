import type { PendingInputRemovalState } from "../chat/ChatCommands.ts";
import type { SessionClient } from "../sessions/SessionClient.ts";
import type { SessionSnapshot } from "../sessions/SessionSnapshot.ts";

export function pendingInputRemovalKey(
  sessionId: string,
  executionId: string,
  submissionId: string,
): string {
  return JSON.stringify([sessionId, executionId, submissionId]);
}

interface PendingInputRemovalAccess {
  get(key: string): PendingInputRemovalState | undefined;
  set(key: string, state: PendingInputRemovalState | null): void;
  knownExecutionId(id: string): string | null;
  observeSnapshot(snapshot: SessionSnapshot, knownWhenRead: string | null): void;
  refreshSnapshot(): void;
}

export class PendingInputRemovalCommands {
  private readonly requests = new Set<string>();
  private readonly client: Pick<SessionClient, "removePendingInput" | "getSnapshot">;
  private readonly access: PendingInputRemovalAccess;

  constructor(
    client: Pick<SessionClient, "removePendingInput" | "getSnapshot">,
    access: PendingInputRemovalAccess,
  ) {
    this.client = client;
    this.access = access;
  }

  async remove(
    id: string,
    executionId: string,
    submissionId: string,
    canRemove: boolean,
  ): Promise<void> {
    if (!canRemove || !id || !executionId || !submissionId) return;
    const key = pendingInputRemovalKey(id, executionId, submissionId);
    if (this.requests.has(key) || this.access.get(key)) return;
    this.requests.add(key);
    this.access.set(key, { sessionId: id, executionId, submissionId, status: "removing" });
    try {
      await this.client.removePendingInput(id, executionId, submissionId);
    } catch {
      // Reconcile the latest snapshot before offering another removal attempt.
    }
    this.access.set(key, { sessionId: id, executionId, submissionId, status: "uncertain" });
    try {
      await this.readSnapshot(id, key);
    } catch {
      // Keep the action disabled until a later snapshot read resolves its state.
    } finally {
      this.requests.delete(key);
      this.access.refreshSnapshot();
    }
  }

  async check(id: string, executionId: string, submissionId: string): Promise<void> {
    const key = pendingInputRemovalKey(id, executionId, submissionId);
    const current = this.access.get(key);
    if (current?.status !== "uncertain" || this.requests.has(key)) return;
    this.requests.add(key);
    this.access.set(key, { ...current, status: "checking" });
    try {
      await this.readSnapshot(id, key);
    } catch {
      this.access.set(key, { ...current, status: "uncertain" });
    } finally {
      this.requests.delete(key);
      this.access.refreshSnapshot();
    }
  }

  private async readSnapshot(id: string, key: string): Promise<void> {
    const knownWhenRead = this.access.knownExecutionId(id);
    const snapshot = await this.client.getSnapshot(id);
    if (
      snapshot.sessionId !== id ||
      !Number.isSafeInteger(snapshot.historyRevision) ||
      typeof snapshot.canChangeWorkingDirectory !== "boolean"
    )
      throw new TypeError("Invalid session snapshot after pending-input removal.");
    this.access.observeSnapshot(snapshot, knownWhenRead);
    // The view's snapshot owner refreshes after this read; publishing it here
    // could overwrite a newer view or a receipt accepted while the GET was in flight.
    this.access.set(key, null);
  }
}
