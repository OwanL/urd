import { RequestError } from "../http/RequestError.ts";
import { newUuid } from "../Uuid.ts";
import type { ChatCompactionState } from "../chat/ChatCommands.ts";
import type { CompactionReceipt } from "../sessions/inputs/Compaction.ts";
import type { SessionClient } from "../sessions/SessionClient.ts";
import type { SessionSnapshot } from "../sessions/SessionSnapshot.ts";

type CompactionClient = Pick<
  SessionClient,
  "startCompaction" | "getCompactionOperation" | "cancelCompaction"
>;
interface CompactionOperations {
  get(id: string): ChatCompactionState | undefined;
  set(id: string, state: ChatCompactionState): void;
  refreshSnapshot(): void;
}

// Owns compaction dispatch and operation reads; admission and status stay with the workspace.
export class CompactionCommands {
  private readonly compactionReads = new Map<string, boolean>();
  private readonly cancellations = new Set<string>();
  private readonly generations = new Map<string, number>();

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
    const operationId = newUuid();
    this.operations.set(id, { phase: "starting", operationId });
    try {
      const receipt = await this.client.startCompaction(id, {
        operationId,
        expectedHistoryRevision: snapshot.historyRevision,
        llmSelection: selection,
      });
      const current = this.operations.get(id);
      if (current?.phase === "starting" && current.operationId === operationId)
        this.applyCompactionReceipt(id, operationId, receipt);
    } catch (cause) {
      const current = this.operations.get(id);
      if (current?.phase !== "starting" || current.operationId !== operationId) return;
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

  async cancel(id: string, operationId: string): Promise<void> {
    if (this.cancellations.has(operationId)) return;
    this.cancellations.add(operationId);
    this.generations.set(operationId, (this.generations.get(operationId) ?? 0) + 1);
    this.operations.set(id, { phase: "observing", operationId, queued: true, cancelling: true });
    try {
      const receipt = await this.client.cancelCompaction(id, operationId);
      if (receipt.executionId !== operationId || receipt.outcome !== "cancelled")
        throw new Error("Cancellation was not acknowledged.");
      if (this.observesCompaction(id, operationId))
        this.operations.set(id, {
          phase: "settled",
          message: receipt.error ?? "Compaction stopped.",
          cancelledOperationId: operationId,
        });
    } catch {
      if (this.observesCompaction(id, operationId)) {
        this.operations.set(id, {
          phase: "uncertain",
          operationId,
          message: "Cancellation could not be confirmed.",
        });
        void this.check(id, operationId);
      }
    } finally {
      this.cancellations.delete(operationId);
      this.operations.refreshSnapshot();
    }
  }

  async check(id: string, operationId: string): Promise<void> {
    if (this.compactionReads.has(operationId)) {
      this.compactionReads.set(operationId, true);
      return;
    }
    this.compactionReads.set(operationId, false);
    const generation = this.generations.get(operationId) ?? 0;
    try {
      const receipt = await this.client.getCompactionOperation(id, operationId);
      if (
        (this.generations.get(operationId) ?? 0) !== generation ||
        !this.observesCompaction(id, operationId)
      )
        return;
      if (receipt) this.applyCompactionReceipt(id, operationId, receipt);
      else this.operations.set(id, { phase: "settled", message: "Compaction was not confirmed." });
    } catch {
      if (
        (this.generations.get(operationId) ?? 0) === generation &&
        this.observesCompaction(id, operationId)
      )
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
    this.operations.set(id, {
      phase: "settled",
      message,
      ...(receipt.outcome === "cancelled" && this.generations.has(operationId)
        ? { cancelledOperationId: operationId }
        : {}),
    });
  }
}
