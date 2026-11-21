import { RequestError } from "../http/RequestError.ts";
import type { ChatCompactionState } from "../chat/ChatCommands.ts";
import type { CompactionReceipt } from "../sessions/inputs/Compaction.ts";
import type { SessionClient } from "../sessions/SessionClient.ts";
import type { SessionSnapshot } from "../sessions/SessionSnapshot.ts";

type CompactionClient = Pick<SessionClient, "startCompaction" | "getCompactionOperation">;
interface CompactionOperations {
  get(id: string): ChatCompactionState | undefined;
  set(id: string, state: ChatCompactionState): void;
  refreshSnapshot(): void;
}

// Owns compaction dispatch and operation reads; admission and status stay with the workspace.
export class CompactionCommands {
  private readonly compactionReads = new Map<string, boolean>();

  private readonly client: CompactionClient;
  private readonly operations: CompactionOperations;

  constructor(client: CompactionClient, operations: CompactionOperations) {
    this.client = client;
    this.operations = operations;
  }

  async start(
    id: string,
    snapshot: SessionSnapshot,
    selection: NonNullable<SessionSnapshot["latestLlmSelection"]>,
  ): Promise<void> {
    const operationId = crypto.randomUUID();
    this.operations.set(id, { phase: "starting", operationId });
    try {
      const receipt = await this.client.startCompaction(id, {
        operationId,
        expectedHistoryRevision: snapshot.historyRevision,
        llmSelection: selection,
      });
      this.applyCompactionReceipt(id, operationId, receipt);
    } catch (cause) {
      if (cause instanceof RequestError && cause.status === 409)
        this.operations.set(id, {
          phase: "settled",
          message: "Session changed. Compaction not started.",
        });
      else
        this.operations.set(id, {
          phase: "uncertain",
          operationId,
          message: "Compaction acknowledgement uncertain.",
        });
    } finally {
      this.operations.refreshSnapshot();
    }
  }

  async check(id: string, operationId: string): Promise<void> {
    if (this.compactionReads.has(operationId)) {
      this.compactionReads.set(operationId, true);
      return;
    }
    this.compactionReads.set(operationId, false);
    try {
      const receipt = await this.client.getCompactionOperation(id, operationId);
      if (!this.observesCompaction(id, operationId)) return;
      if (receipt) this.applyCompactionReceipt(id, operationId, receipt);
      else this.operations.set(id, { phase: "settled", message: "Compaction was not confirmed." });
    } catch {
      if (this.observesCompaction(id, operationId))
        this.operations.set(id, {
          phase: "uncertain",
          operationId,
          message: "Compaction outcome unavailable.",
        });
    } finally {
      const queued = this.compactionReads.get(operationId);
      this.compactionReads.delete(operationId);
      if (queued && this.observesCompaction(id, operationId)) void this.check(id, operationId);
    }
  }

  private observesCompaction(id: string, operationId: string): boolean {
    const operation = this.operations.get(id);
    return (
      (operation?.phase === "observing" || operation?.phase === "uncertain") &&
      operation.operationId === operationId
    );
  }

  private applyCompactionReceipt(
    id: string,
    operationId: string,
    receipt: CompactionReceipt,
  ): void {
    if (receipt.executionId !== operationId) throw new Error("Unexpected compaction receipt.");
    if (receipt.outcome === null) {
      this.operations.set(id, { phase: "observing", operationId, queued: receipt.queued === true });
      return;
    }
    const message =
      receipt.outcome === "completed"
        ? (receipt.error ?? "History compacted.")
        : receipt.outcome === "cancelled"
          ? (receipt.error ?? "Compaction stopped.")
          : (receipt.error ?? "Compaction failed. Previous context kept.");
    this.operations.set(id, { phase: "settled", message });
  }
}
