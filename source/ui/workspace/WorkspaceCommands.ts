import { CompactionCommands } from "./CompactionCommands.ts";
import {
  PendingInputRemovalCommands,
  pendingInputRemovalKey,
} from "./PendingInputRemovalCommands.ts";
export { pendingInputRemovalKey } from "./PendingInputRemovalCommands.ts";
import { SubmissionCommands } from "./SubmissionCommands.ts";
import { RequestError } from "../http/RequestError.ts";
import type {
  ChatCompactionState,
  ChatStopState,
  PendingInputRemovalState,
} from "../chat/ChatCommands.ts";
import type { MessageEditingAccess } from "../chat/message-editing/MessageEditingAccess.ts";
import type { ChatState } from "../chat/ChatState.ts";
export type { PendingInputRemovalState } from "../chat/ChatCommands.ts";
export type { ChatStopState as WorkspaceStopState } from "../chat/ChatCommands.ts";
import type {
  SubagentProviderConnectionIds,
  SubagentProviders,
} from "../providers/subagents/SubagentProviderSelection.ts";
import type { ContinuationReceipt } from "../sessions/inputs/Continuation.ts";
import { executionSettled, idleChatSelection, newChat } from "../chat/ChatState.ts";
import type { LlmSelection } from "../providers/LlmSelection.ts";

import type { WorkspaceState } from "./WorkspaceState.ts";
import type { SessionClient } from "../sessions/SessionClient.ts";
import type { SessionSnapshot } from "../sessions/SessionSnapshot.ts";
import type {
  UserMessageReceipt,
  UserMessageSubmission,
} from "../sessions/inputs/UserMessageSubmission.ts";

type WorkspaceStopState = ChatStopState;
type WorkspaceCompactionState = ChatCompactionState;

export interface WorkspaceCommandState {
  readonly creating: boolean;
  readonly closing: ReadonlySet<string>;
  readonly closeError: { readonly sessionId: string; readonly message: string } | null;
  readonly savingSubagentProviders: ReadonlySet<string>;
  readonly subagentProviderError: { readonly sessionId: string; readonly message: string } | null;
  readonly compactions: Readonly<Record<string, WorkspaceCompactionState>>;
  readonly stopping: Readonly<Record<string, WorkspaceStopState>>;
  readonly checking: Readonly<Record<string, boolean>>;
  readonly checkingContinuations: Readonly<Record<string, boolean>>;
  readonly pendingInputRemovals: Readonly<Record<string, PendingInputRemovalState>>;
}

export interface NotSentResendResult {
  readonly submissionId: string;
  readonly receipt: UserMessageReceipt | null;
}

export interface WorkspaceCommandAccess extends MessageEditingAccess {
  read(): WorkspaceState;
  save(state: WorkspaceState): boolean;
  updateChat(
    id: string,
    update: (previous: ChatState) => ChatState,
    lastSelection?: LlmSelection,
  ): boolean;
}

export const emptyWorkspaceCommandState: WorkspaceCommandState = {
  creating: false,
  closing: new Set(),
  closeError: null,
  savingSubagentProviders: new Set(),
  subagentProviderError: null,
  compactions: {},
  stopping: {},
  checking: {},
  checkingContinuations: {},
  pendingInputRemovals: {},
};

type WorkspaceCommandClient = Pick<
  SessionClient,
  | "create"
  | "closeSession"
  | "submitMessage"
  | "getMessageReceipt"
  | "getSnapshot"
  | "setSubagentProviders"
  | "continueSession"
  | "getContinuationReceipt"
  | "startCompaction"
  | "cancelCompaction"
  | "getCompactionOperation"
  | "removePendingInput"
  | "stopExecution"
>;

// Coordinates durable workspace commands without retrying uncertain dispatches.
export class WorkspaceCommands {
  private readonly submissions: SubmissionCommands;
  private readonly compactions: CompactionCommands;
  private readonly removals: PendingInputRemovalCommands;
  private readonly client: WorkspaceCommandClient;
  private readonly access: WorkspaceCommandAccess;
  private readonly changed: (state: WorkspaceCommandState) => void;
  private readonly reportError: (message: string | null) => void;
  private readonly refreshSnapshot: () => void;
  private readonly preferenceSaveBlocks: (command: "create" | "execute") => boolean;
  private state = emptyWorkspaceCommandState;
  private createPending = false;
  private readonly checks = new Set<string>();
  private readonly receiptReads = new Map<string, AbortController>();
  private readonly continuationChecks = new Set<string>();
  private readonly continuationReceiptReads = new Map<string, AbortController>();
  private readonly stoppingRequests = new Set<string>();

  constructor(
    client: WorkspaceCommandClient,
    access: WorkspaceCommandAccess,
    changed: (state: WorkspaceCommandState) => void,
    reportError: (message: string | null) => void,
    refreshSnapshot: () => void,
    preferenceSaveBlocks: (command: "create" | "execute") => boolean = () => false,
  ) {
    this.client = client;
    this.access = access;
    this.changed = changed;
    this.reportError = reportError;
    this.refreshSnapshot = refreshSnapshot;
    this.preferenceSaveBlocks = preferenceSaveBlocks;
    this.removals = new PendingInputRemovalCommands(client, {
      get: (key) => this.state.pendingInputRemovals[key],
      set: (key, state) => this.setPendingInputRemoval(key, state),
      knownExecutionId: (id) => this.access.read().chats[id]?.knownExecutionId ?? null,
      observeSnapshot: (snapshot, knownWhenRead) => this.observeSnapshot(snapshot, knownWhenRead),
      refreshSnapshot,
    });
    this.compactions = new CompactionCommands(client, {
      get: (id) => this.state.compactions[id],
      set: (id, state) => this.setCompaction(id, state),
      refreshSnapshot,
    });
    this.submissions = new SubmissionCommands(client, access, {
      executionSettingsSaving: (id) => this.executionSettingsSaving(id),
      isCompacting: (id) => this.isCompacting(id),
      isStopping: (id) => !!this.state.stopping[id],
      hasPendingInputRemoval: (id) => this.hasPendingInputRemoval(id),
      isChecking: (id) => this.checks.has(id),
      isCheckingContinuation: (id) => this.continuationChecks.has(id),
      beginReceiptRead: (id, controller) => {
        if (controller) this.receiptReads.set(id, controller);
        this.checks.add(id);
        this.setChecking(id, true);
      },
      endReceiptRead: (id) => {
        this.checks.delete(id);
        this.receiptReads.delete(id);
        this.setChecking(id, false);
      },
      beginContinuationReceiptRead: (id, controller) => {
        if (controller) this.continuationReceiptReads.set(id, controller);
        this.continuationChecks.add(id);
        this.setCheckingContinuation(id, true);
      },
      endContinuationReceiptRead: (id) => {
        this.continuationChecks.delete(id);
        this.continuationReceiptReads.delete(id);
        this.setCheckingContinuation(id, false);
      },
      refreshSnapshot,
    });
  }

  get status(): WorkspaceCommandState {
    return this.state;
  }

  pendingInputRemoval(
    sessionId: string,
    executionId: string,
    submissionId: string,
  ): PendingInputRemovalState | undefined {
    return this.state.pendingInputRemovals[
      pendingInputRemovalKey(sessionId, executionId, submissionId)
    ];
  }

  hasPendingInputRemoval(sessionId: string): boolean {
    return Object.values(this.state.pendingInputRemovals).some(
      (removal) => removal.sessionId === sessionId,
    );
  }

  async saveSubagentProviders(
    id: string,
    connectionIds: SubagentProviderConnectionIds,
  ): Promise<SubagentProviders | null> {
    if (this.state.closing.has(id) || this.state.savingSubagentProviders.has(id)) return null;
    this.publish({
      savingSubagentProviders: new Set([...this.state.savingSubagentProviders, id]),
      subagentProviderError:
        this.state.subagentProviderError?.sessionId === id
          ? null
          : this.state.subagentProviderError,
    });
    try {
      return await this.client.setSubagentProviders(id, connectionIds);
    } catch (cause) {
      this.publish({
        subagentProviderError: {
          sessionId: id,
          message: `Subagent provider change could not be confirmed. ${cause instanceof RequestError ? requestErrorDescription(cause) : description(cause)}`,
        },
      });
      return null;
    } finally {
      const next = new Set(this.state.savingSubagentProviders);
      next.delete(id);
      this.publish({ savingSubagentProviders: next });
    }
  }

  private executionSettingsSaving(id: string): boolean {
    return this.state.savingSubagentProviders.has(id) || this.preferenceSaveBlocks("execute");
  }

  async create(isClosed: (id: string) => boolean = () => false): Promise<void> {
    if (!this.access.canPersist() || this.createPending || this.preferenceSaveBlocks("create"))
      return;
    if (!this.access.save(this.access.read())) return;

    this.createPending = true;
    this.publish({ creating: true });
    this.reportError(null);
    try {
      const id = await this.client.create();
      if (isClosed(id)) return;
      const state = this.access.read();
      this.access.save({
        ...state,
        activeId: id,
        tabs: state.tabs.includes(id) ? state.tabs : [...state.tabs, id],
        chats: { ...state.chats, [id]: state.chats[id] ?? newChat(null) },
      });
    } catch (cause) {
      this.reportError(`Chat creation is uncertain: ${description(cause)}`);
    } finally {
      this.createPending = false;
      this.publish({ creating: false });
    }
  }

  canClose(id: string, status: string | undefined): boolean {
    const workspace = this.access.read();
    const chat = workspace.chats[id];
    const compaction = this.state.compactions[id];
    return Boolean(
      (status === "idle" || status === "failed") &&
      workspace.tabs.includes(id) &&
      chat &&
      this.access.canPersist() &&
      !chat.pending &&
      !chat.pendingContinuation &&
      !chat.messageEdit &&
      !chat.knownExecutionId &&
      !this.state.closing.has(id) &&
      !this.state.stopping[id] &&
      !this.state.savingSubagentProviders.has(id) &&
      (!compaction || compaction.phase === "settled") &&
      !this.hasPendingInputRemoval(id),
    );
  }

  async close(id: string, status: string | undefined, forget: (id: string) => void): Promise<void> {
    if (!this.canClose(id, status)) return;
    this.publish({ closing: new Set([...this.state.closing, id]), closeError: null });
    try {
      await this.client.closeSession(id);
      forget(id);
    } catch (cause) {
      if (!this.access.read().chats[id]) return;
      if (cause instanceof RequestError && cause.status === 409) {
        this.reportCloseError(id, "Chat is busy. Wait for its work to finish before closing it.");
        this.refreshSnapshot();
      } else {
        // Even a 404 can mean the server lacks the close route. Check the session
        // itself before discarding its draft; never resend a destructive request.
        const observed = await this.checkAfterClose(id);
        if (observed === "missing") forget(id);
        else if (this.access.read().chats[id])
          this.reportCloseError(
            id,
            observed === "open"
              ? "Closure could not be confirmed. The chat was still open when checked."
              : "Closure could not be confirmed. Check the chat before trying again.",
          );
      }
    } finally {
      const closing = new Set(this.state.closing);
      closing.delete(id);
      this.publish({ closing });
    }
  }

  private reportCloseError(sessionId: string, message: string): void {
    this.publish({ closeError: { sessionId, message } });
  }

  private async checkAfterClose(id: string): Promise<"open" | "missing" | "unknown"> {
    try {
      const snapshot = await this.client.getSnapshot(id, AbortSignal.timeout(5_000));
      return snapshot.sessionId === id ? "open" : "unknown";
    } catch (cause) {
      return cause instanceof RequestError && cause.status === 404 ? "missing" : "unknown";
    }
  }

  send(
    id: string,
    snapshot: SessionSnapshot,
    active: boolean,
    canSend: boolean,
    defaultSelection: LlmSelection | null = null,
  ): Promise<void> {
    if (this.state.closing.has(id)) return Promise.resolve();
    return this.submissions.send(id, snapshot, active, canSend, defaultSelection);
  }
  continueSession(
    id: string,
    snapshot: SessionSnapshot,
    canContinue: boolean,
    defaultSelection: LlmSelection | null = null,
  ): Promise<void> {
    if (this.state.closing.has(id)) return Promise.resolve();
    return this.submissions.continueSession(id, snapshot, canContinue, defaultSelection);
  }
  resendNotSent(
    id: string,
    snapshot: SessionSnapshot,
    source: UserMessageSubmission,
  ): Promise<NotSentResendResult | null> {
    if (this.state.closing.has(id)) return Promise.resolve(null);
    return this.submissions.resendNotSent(id, snapshot, source);
  }
  checkReceipt(id: string): Promise<UserMessageReceipt | null> {
    return this.submissions.checkReceipt(id);
  }
  checkContinuationReceipt(id: string): Promise<ContinuationReceipt | null> {
    return this.submissions.checkContinuationReceipt(id);
  }
  retryOriginalContinuation(id: string): Promise<ContinuationReceipt | null> {
    return this.submissions.retryOriginalContinuation(id);
  }
  retryOriginalSubmission(id: string): Promise<UserMessageReceipt | null> {
    return this.submissions.retryOriginalSubmission(id);
  }

  async compact(
    id: string,
    snapshot: SessionSnapshot,
    canCompact: boolean,
    defaultSelection: LlmSelection | null = null,
  ): Promise<void> {
    const chat = this.access.read().chats[id];
    const selection = idleChatSelection(chat, snapshot, defaultSelection);
    if (
      !canCompact ||
      this.state.closing.has(id) ||
      this.executionSettingsSaving(id) ||
      snapshot.sessionId !== id ||
      snapshot.canCompact !== true ||
      snapshot.admissionFenced ||
      snapshot.stopRequested ||
      !chat ||
      !this.access.canPersist() ||
      !selection ||
      chat.pending ||
      chat.pendingContinuation ||
      chat.messageEdit ||
      this.state.stopping[id] ||
      this.hasPendingInputRemoval(id) ||
      (this.state.compactions[id] && this.state.compactions[id].phase !== "settled")
    )
      return;

    await this.compactions.start(id, snapshot, selection);
  }

  cancelCompaction(id: string, snapshot: SessionSnapshot): Promise<void> {
    if (snapshot.sessionId !== id || snapshot.manualCompactionId) return Promise.resolve();
    const current = this.state.compactions[id];
    const operationId =
      snapshot.queuedCompactionId ??
      (current?.phase === "observing" && current.queued ? current.operationId : null);
    if (
      !operationId ||
      (current?.phase === "settled" && current.cancelledOperationId === operationId)
    )
      return Promise.resolve();
    return this.compactions.cancel(id, operationId);
  }

  private isCompacting(id: string): boolean {
    const operation = this.state.compactions[id];
    return (
      operation?.phase === "starting" || (operation?.phase === "observing" && !operation.queued)
    );
  }

  private setCompaction(id: string, compaction: WorkspaceCompactionState): void {
    this.publish({ compactions: { ...this.state.compactions, [id]: compaction } });
  }

  removePendingInput(
    id: string,
    executionId: string,
    submissionId: string,
    canRemove: boolean,
  ): Promise<void> {
    if (this.state.closing.has(id)) return Promise.resolve();
    return this.removals.remove(id, executionId, submissionId, canRemove);
  }

  checkPendingInputRemoval(id: string, executionId: string, submissionId: string): Promise<void> {
    return this.removals.check(id, executionId, submissionId);
  }

  async stop(id: string, snapshot: SessionSnapshot, stopId: string | undefined): Promise<void> {
    const executionId = snapshot.unsettledExecutionId;
    if (!id || !executionId || stopId || this.state.closing.has(id)) return;
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
    const compaction = this.state.compactions[snapshot.sessionId];
    const observedId = snapshot.manualCompactionId ?? snapshot.queuedCompactionId;
    if (
      observedId &&
      (!compaction ||
        (compaction.phase === "settled" && compaction.cancelledOperationId !== observedId))
    ) {
      this.setCompaction(snapshot.sessionId, {
        phase: "observing",
        operationId: observedId,
        queued: observedId === snapshot.queuedCompactionId,
      });
    }
    if (
      (compaction?.phase === "observing" || compaction?.phase === "uncertain") &&
      snapshot.unsettledExecutionId !== compaction.operationId &&
      snapshot.queuedCompactionId !== compaction.operationId &&
      snapshot.manualCompactionId !== compaction.operationId
    )
      void this.compactions.check(snapshot.sessionId, compaction.operationId);

    const saved = this.access.read().chats[snapshot.sessionId];
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
    const continuationReads = this.continuationReceiptReads;
    const checkPending = () => {
      if (document.hidden) {
        for (const controller of reads.values()) controller.abort();
        for (const controller of continuationReads.values()) controller.abort();
        return;
      }
      for (const [id, chat] of Object.entries(this.access.read().chats)) {
        if (chat.pending?.status === "uncertain") void this.checkReceipt(id);
        if (chat.pendingContinuation?.status === "uncertain")
          void this.checkContinuationReceipt(id);
      }
    };
    checkPending();
    window.addEventListener("focus", checkPending);
    document.addEventListener("visibilitychange", checkPending);
    return () => {
      for (const controller of reads.values()) controller.abort();
      for (const controller of continuationReads.values()) controller.abort();
      window.removeEventListener("focus", checkPending);
      document.removeEventListener("visibilitychange", checkPending);
    };
  }

  private setCheckingContinuation(id: string, checking: boolean): void {
    const next = { ...this.state.checkingContinuations };
    if (checking) next[id] = true;
    else delete next[id];
    this.publish({ checkingContinuations: next });
  }

  private setPendingInputRemoval(key: string, state: PendingInputRemovalState | null): void {
    const next = { ...this.state.pendingInputRemovals };
    if (state) next[key] = state;
    else delete next[key];
    this.publish({ pendingInputRemovals: next });
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

function requestErrorDescription(error: RequestError): string {
  const body = error.responseBody;
  if (typeof body === "object" && body !== null && "error" in body) {
    const message = body.error;
    if (typeof message === "string" && message.trim()) return message;
  }
  return error.message;
}

function description(cause: unknown): string {
  return cause instanceof Error ? cause.message : String(cause);
}
