import {
  applyReceipt,
  beginSubmission,
  executionSettled,
  newChat,
  submissionTextError,
} from "./WorkspaceState.ts";
import type { ChatState, WorkspaceState } from "./WorkspaceState.ts";
import type { SessionClient } from "../sessions/SessionClient.ts";
import type { SessionSnapshot } from "../sessions/SessionSnapshot.ts";
import type { UserMessageSubmission } from "../sessions/UserMessageSubmission.ts";

export interface WorkspaceStopState {
  readonly executionId: string;
  readonly pending: boolean;
  readonly error: string | null;
}

export interface WorkspaceCommandState {
  readonly creating: boolean;
  readonly stopping: Readonly<Record<string, WorkspaceStopState>>;
  readonly checking: Readonly<Record<string, boolean>>;
}

export interface WorkspaceCommandAccess {
  read(): WorkspaceState;
  save(state: WorkspaceState): boolean;
  updateChat(id: string, update: (previous: ChatState) => ChatState): boolean;
  canPersist(): boolean;
}

export const emptyWorkspaceCommandState: WorkspaceCommandState = {
  creating: false,
  stopping: {},
  checking: {},
};

type WorkspaceCommandClient = Pick<
  SessionClient,
  "create" | "submitMessage" | "getMessageReceipt" | "stopExecution"
>;

// Coordinates durable workspace commands without retrying uncertain dispatches.
export class WorkspaceCommands {
  private readonly client: WorkspaceCommandClient;
  private readonly access: WorkspaceCommandAccess;
  private readonly changed: (state: WorkspaceCommandState) => void;
  private readonly reportError: (message: string | null) => void;
  private readonly refreshSnapshot: () => void;
  private state = emptyWorkspaceCommandState;
  private createPending = false;
  private readonly checks = new Set<string>();
  private readonly receiptReads = new Map<string, AbortController>();
  private readonly stoppingRequests = new Set<string>();

  constructor(
    client: WorkspaceCommandClient,
    access: WorkspaceCommandAccess,
    changed: (state: WorkspaceCommandState) => void,
    reportError: (message: string | null) => void,
    refreshSnapshot: () => void,
  ) {
    this.client = client;
    this.access = access;
    this.changed = changed;
    this.reportError = reportError;
    this.refreshSnapshot = refreshSnapshot;
  }

  get status(): WorkspaceCommandState {
    return this.state;
  }

  async create(): Promise<void> {
    if (!this.access.canPersist() || this.createPending) return;
    if (!this.access.save(this.access.read())) return;

    this.createPending = true;
    this.publish({ creating: true });
    this.reportError(null);
    try {
      const id = await this.client.create();
      const state = this.access.read();
      this.access.save({
        ...state,
        activeId: id,
        tabs: [...state.tabs, id],
        chats: { ...state.chats, [id]: newChat(state.lastSelection) },
      });
    } catch (cause) {
      this.reportError(`Chat creation is uncertain: ${description(cause)}`);
    } finally {
      this.createPending = false;
      this.publish({ creating: false });
    }
  }

  async send(
    id: string,
    snapshot: SessionSnapshot,
    active: boolean,
    canSend: boolean,
  ): Promise<void> {
    if (!canSend || !this.access.canPersist()) return;
    const latest = this.access.read().chats[id];
    const selection = active
      ? snapshot.latestLlmSelection
      : (latest?.selection ?? snapshot.latestLlmSelection);
    if (
      !latest ||
      latest.pending ||
      !selection ||
      !latest.draft.trim() ||
      submissionTextError(latest.draft)
    )
      return;

    const original: UserMessageSubmission = {
      submissionId: crypto.randomUUID(),
      expectedHistoryRevision: snapshot.historyRevision,
      text: latest.draft,
      llmSelection: selection,
    };
    const persisted = this.access.updateChat(id, (previous) => ({
      ...beginSubmission(previous, original),
      title:
        previous.title === "New chat"
          ? [...original.text.trim()].slice(0, 48).join("")
          : previous.title,
    }));
    if (!persisted) {
      this.access.updateChat(id, (previous) =>
        previous.pending
          ? {
              ...previous,
              pending: { ...previous.pending, status: "uncertain" },
              error: "Send was not dispatched because the journal could not be saved.",
            }
          : previous,
      );
      return;
    }
    await this.reconcile(id, true);
  }

  async reconcile(id: string, retry: boolean): Promise<void> {
    const original = this.access.read().chats[id]?.pending?.original;
    if (
      !original ||
      this.checks.has(id) ||
      (retry && !this.access.canPersist()) ||
      (!retry && document.hidden)
    )
      return;

    const controller = retry ? null : new AbortController();
    if (controller) this.receiptReads.set(id, controller);
    this.checks.add(id);
    this.setChecking(id, true);
    try {
      if (
        retry &&
        !this.access.updateChat(id, (previous) =>
          previous.pending
            ? { ...previous, pending: { ...previous.pending, status: "sending" }, error: null }
            : previous,
        )
      )
        throw new Error("The original Send could not be journaled; it was not dispatched.");
      const receipt = retry
        ? await this.client.submitMessage(id, original)
        : await this.client.getMessageReceipt(id, original.submissionId, controller?.signal);
      if (receipt === null) throw new Error("No receipt found. Send remains uncertain.");
      this.access.updateChat(id, (previous) => applyReceipt(previous, id, receipt));
    } catch (cause) {
      if (controller?.signal.aborted) return;
      this.access.updateChat(id, (previous) =>
        previous.pending
          ? {
              ...previous,
              pending: { ...previous.pending, status: "uncertain" },
              error: description(cause),
            }
          : previous,
      );
    } finally {
      this.checks.delete(id);
      this.receiptReads.delete(id);
      this.setChecking(id, false);
      this.refreshSnapshot();
    }
  }

  async stop(id: string, snapshot: SessionSnapshot, stopId: string | undefined): Promise<void> {
    const executionId = snapshot.unsettledExecutionId;
    if (!id || !executionId || stopId) return;
    const requestKey = `${id}/${executionId}`;
    if (this.stoppingRequests.has(requestKey)) return;
    this.stoppingRequests.add(requestKey);
    this.setStopping(id, { executionId, pending: true, error: null });
    try {
      const receipt = await this.client.stopExecution(id, executionId);
      if (
        receipt.executionId !== executionId ||
        (receipt.status !== "requested" && receipt.status !== "settled")
      )
        throw new Error("Stop was not acknowledged.");
    } catch (cause) {
      if (this.state.stopping[id]?.executionId === executionId)
        this.setStopping(id, {
          executionId,
          pending: false,
          error: `Stop acknowledgement uncertain: ${description(cause)}`,
        });
    } finally {
      this.stoppingRequests.delete(requestKey);
      this.refreshSnapshot();
    }
  }

  observeSnapshot(snapshot: SessionSnapshot, knownWhenRead: string | null): void {
    const saved = this.access.read().chats[snapshot.sessionId];
    if (saved?.title === "New chat" && snapshot.messages.length > 0) {
      const prompt = snapshot.messages.find((message) => message.message.role === "user");
      const title = [...(prompt?.preview.trim() ?? "Chat")].slice(0, 48).join("");
      this.access.updateChat(snapshot.sessionId, (previous) => ({
        ...previous,
        title: title || "Chat",
      }));
    }
    if (
      saved?.knownExecutionId &&
      executionSettled(snapshot, saved.knownExecutionId, knownWhenRead)
    )
      this.access.updateChat(snapshot.sessionId, (previous) => ({
        ...previous,
        knownExecutionId: null,
      }));

    const stopping = this.state.stopping[snapshot.sessionId];
    if (
      stopping &&
      (!snapshot.unsettledExecutionId ||
        snapshot.lastSettlement?.executionId === stopping.executionId)
    ) {
      this.setStopping(snapshot.sessionId, null);
    }
  }

  watchPendingReceipts(): () => void {
    const reads = this.receiptReads;
    const checkPending = () => {
      if (document.hidden) {
        for (const controller of reads.values()) controller.abort();
        return;
      }
      for (const [id, chat] of Object.entries(this.access.read().chats))
        if (chat.pending?.status === "uncertain") void this.reconcile(id, false);
    };
    checkPending();
    window.addEventListener("focus", checkPending);
    document.addEventListener("visibilitychange", checkPending);
    return () => {
      for (const controller of reads.values()) controller.abort();
      window.removeEventListener("focus", checkPending);
      document.removeEventListener("visibilitychange", checkPending);
    };
  }

  private setChecking(id: string, checking: boolean): void {
    const next = { ...this.state.checking };
    if (checking) next[id] = true;
    else delete next[id];
    this.publish({ checking: next });
  }

  private setStopping(id: string, state: WorkspaceStopState | null): void {
    const next = { ...this.state.stopping };
    if (state) next[id] = state;
    else delete next[id];
    this.publish({ stopping: next });
  }

  private publish(update: Partial<WorkspaceCommandState>): void {
    this.state = { ...this.state, ...update };
    this.changed(this.state);
  }
}

function description(cause: unknown): string {
  return cause instanceof Error ? cause.message : String(cause);
}
