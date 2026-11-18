import { RequestError } from "../http/RequestError.ts";
import type {
  SubagentProviderConnectionIds,
  SubagentProviders,
} from "../providers/SubagentProviderSelection.ts";
import type { ContinuationReceipt, ContinuationSubmission } from "../sessions/Continuation.ts";
import type { CompactionReceipt } from "../sessions/Compaction.ts";
import {
  applyContinuationReceipt,
  applyReceipt,
  beginContinuation,
  beginSubmission,
  executionSettled,
  newChat,
  submissionTextError,
} from "./WorkspaceState.ts";
import type { ChatState, WorkspaceState } from "./WorkspaceState.ts";
import type { SessionClient } from "../sessions/SessionClient.ts";
import type { SessionSnapshot } from "../sessions/SessionSnapshot.ts";
import type {
  UserMessageReceipt,
  UserMessageSubmission,
} from "../sessions/UserMessageSubmission.ts";

export interface WorkspaceStopState {
  readonly executionId: string;
  readonly pending: boolean;
  readonly error: string | null;
}

export interface PendingInputRemovalState {
  readonly sessionId: string;
  readonly executionId: string;
  readonly submissionId: string;
  readonly status: "removing" | "checking" | "uncertain";
}

type WorkspaceCompactionState =
  | { readonly phase: "starting" | "observing"; readonly operationId: string }
  | { readonly phase: "uncertain"; readonly operationId: string; readonly message: string }
  | { readonly phase: "settled"; readonly message: string };

export interface WorkspaceCommandState {
  readonly creating: boolean;
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

export interface WorkspaceCommandAccess {
  read(): WorkspaceState;
  save(state: WorkspaceState): boolean;
  updateChat(id: string, update: (previous: ChatState) => ChatState): boolean;
  canPersist(): boolean;
}

export const emptyWorkspaceCommandState: WorkspaceCommandState = {
  creating: false,
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
  | "submitMessage"
  | "getMessageReceipt"
  | "getSnapshot"
  | "setSubagentProviders"
  | "continueSession"
  | "getContinuationReceipt"
  | "startCompaction"
  | "getCompactionOperation"
  | "removePendingInput"
  | "stopExecution"
>;

export function pendingInputRemovalKey(
  sessionId: string,
  executionId: string,
  submissionId: string,
): string {
  return JSON.stringify([sessionId, executionId, submissionId]);
}

// Coordinates durable workspace commands without retrying uncertain dispatches.
export class WorkspaceCommands {
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
  private readonly pendingInputRemovals = new Set<string>();
  private readonly stoppingRequests = new Set<string>();
  private readonly compactionReads = new Map<string, boolean>();

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
    if (this.state.savingSubagentProviders.has(id)) return null;
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

  async create(): Promise<void> {
    if (!this.access.canPersist() || this.createPending || this.preferenceSaveBlocks("create"))
      return;
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
        tabs: state.tabs.includes(id) ? state.tabs : [...state.tabs, id],
        chats: { ...state.chats, [id]: state.chats[id] ?? newChat(state.lastSelection) },
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
    if (
      !canSend ||
      !this.access.canPersist() ||
      this.isCompacting(id) ||
      this.executionSettingsSaving(id)
    )
      return;
    const latest = this.access.read().chats[id];
    const selection = active
      ? snapshot.latestLlmSelection
      : (latest?.selection ?? snapshot.latestLlmSelection);
    if (
      !latest ||
      latest.pending ||
      latest.pendingContinuation ||
      latest.messageEdit ||
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
    await this.retryOriginalSubmission(id);
  }

  async continueSession(
    id: string,
    snapshot: SessionSnapshot,
    canContinue: boolean,
  ): Promise<void> {
    const latest = this.access.read().chats[id];
    const selection = latest?.selection ?? snapshot.latestLlmSelection;
    const expectedLastExecutionId = snapshot.lastSettlement?.executionId;
    if (
      !canContinue ||
      this.executionSettingsSaving(id) ||
      !this.access.canPersist() ||
      snapshot.sessionId !== id ||
      snapshot.canContinue !== true ||
      snapshot.unsettledExecutionId !== null ||
      snapshot.admissionFenced ||
      snapshot.stopRequested ||
      !expectedLastExecutionId ||
      !latest ||
      !selection ||
      latest.pending ||
      latest.pendingContinuation ||
      latest.messageEdit ||
      latest.knownExecutionId ||
      this.state.stopping[id] ||
      this.hasPendingInputRemoval(id) ||
      this.isCompacting(id)
    )
      return;

    const original: ContinuationSubmission = {
      operationId: crypto.randomUUID(),
      expectedHistoryRevision: snapshot.historyRevision,
      expectedLastExecutionId,
      llmSelection: structuredClone(selection),
    };
    if (!this.access.updateChat(id, (previous) => beginContinuation(previous, original))) {
      this.access.updateChat(id, (previous) =>
        previous.pendingContinuation
          ? {
              ...previous,
              pendingContinuation: { ...previous.pendingContinuation, status: "uncertain" },
              error: "Continue was not dispatched because its journal could not be saved.",
            }
          : previous,
      );
      return;
    }
    await this.retryOriginalContinuation(id);
  }

  async compact(id: string, snapshot: SessionSnapshot, canCompact: boolean): Promise<void> {
    const chat = this.access.read().chats[id];
    const selection = chat?.selection ?? snapshot.latestLlmSelection;
    if (
      !canCompact ||
      this.executionSettingsSaving(id) ||
      snapshot.sessionId !== id ||
      snapshot.canCompact !== true ||
      snapshot.unsettledExecutionId !== null ||
      snapshot.admissionFenced ||
      snapshot.stopRequested ||
      !chat ||
      !this.access.canPersist() ||
      !selection ||
      chat.pending ||
      chat.pendingContinuation ||
      chat.messageEdit ||
      chat.knownExecutionId ||
      this.state.stopping[id] ||
      this.hasPendingInputRemoval(id) ||
      this.isCompacting(id) ||
      this.state.compactions[id]?.phase === "uncertain"
    )
      return;

    const operationId = crypto.randomUUID();
    this.setCompaction(id, { phase: "starting", operationId });
    try {
      const receipt = await this.client.startCompaction(id, {
        operationId,
        expectedHistoryRevision: snapshot.historyRevision,
        llmSelection: selection,
      });
      this.applyCompactionReceipt(id, operationId, receipt);
    } catch (cause) {
      if (cause instanceof RequestError && cause.status === 409)
        this.setCompaction(id, {
          phase: "settled",
          message: "Session changed. Compaction not started.",
        });
      else this.setCompaction(id, { phase: "observing", operationId });
    } finally {
      this.refreshSnapshot();
    }
  }

  private isCompacting(id: string): boolean {
    const phase = this.state.compactions[id]?.phase;
    return phase === "starting" || phase === "observing";
  }

  private async checkCompaction(id: string, operationId: string): Promise<void> {
    if (this.compactionReads.has(operationId)) {
      this.compactionReads.set(operationId, true);
      return;
    }
    this.compactionReads.set(operationId, false);
    try {
      const receipt = await this.client.getCompactionOperation(id, operationId);
      if (!this.observesCompaction(id, operationId)) return;
      if (receipt) this.applyCompactionReceipt(id, operationId, receipt);
      else this.setCompaction(id, { phase: "settled", message: "Compaction was not confirmed." });
    } catch {
      if (this.observesCompaction(id, operationId))
        this.setCompaction(id, {
          phase: "uncertain",
          operationId,
          message: "Compaction outcome unavailable.",
        });
    } finally {
      const queued = this.compactionReads.get(operationId);
      this.compactionReads.delete(operationId);
      if (queued && this.observesCompaction(id, operationId))
        void this.checkCompaction(id, operationId);
    }
  }

  private observesCompaction(id: string, operationId: string): boolean {
    const operation = this.state.compactions[id];
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
      this.setCompaction(id, { phase: "observing", operationId });
      return;
    }
    const message =
      receipt.outcome === "completed"
        ? (receipt.error ?? "History compacted.")
        : receipt.outcome === "cancelled"
          ? "Compaction stopped."
          : "Compaction failed. Previous context kept.";
    this.setCompaction(id, { phase: "settled", message });
  }

  private setCompaction(id: string, compaction: WorkspaceCompactionState): void {
    this.publish({ compactions: { ...this.state.compactions, [id]: compaction } });
  }

  async resendNotSent(
    id: string,
    snapshot: SessionSnapshot,
    source: UserMessageSubmission,
  ): Promise<NotSentResendResult | null> {
    const latest = this.access.read().chats[id];
    if (
      snapshot.sessionId !== id ||
      this.executionSettingsSaving(id) ||
      this.isCompacting(id) ||
      !latest ||
      latest.pending ||
      latest.pendingContinuation ||
      latest.messageEdit ||
      !this.access.canPersist()
    )
      return null;

    const submission: UserMessageSubmission = {
      submissionId: crypto.randomUUID(),
      expectedHistoryRevision: snapshot.historyRevision,
      text: source.text,
      llmSelection: source.llmSelection,
    };
    if (
      !this.access.updateChat(id, (previous) =>
        previous.pending ? previous : beginSubmission(previous, submission, true),
      )
    ) {
      this.access.updateChat(id, (previous) =>
        previous.pending
          ? {
              ...previous,
              pending: { ...previous.pending, status: "uncertain" },
              error: "Resend was not dispatched because the journal could not be saved.",
            }
          : previous,
      );
      return null;
    }
    const receipt = await this.retryOriginalSubmission(id);
    return { submissionId: submission.submissionId, receipt };
  }

  checkReceipt(id: string): Promise<UserMessageReceipt | null> {
    return this.reconcile(id, false);
  }

  checkContinuationReceipt(id: string): Promise<ContinuationReceipt | null> {
    return this.reconcileContinuation(id, false);
  }

  retryOriginalContinuation(id: string): Promise<ContinuationReceipt | null> {
    return this.reconcileContinuation(id, true);
  }

  async removePendingInput(
    id: string,
    executionId: string,
    submissionId: string,
    canRemove: boolean,
  ): Promise<void> {
    if (!canRemove || !id || !executionId || !submissionId) return;
    const key = pendingInputRemovalKey(id, executionId, submissionId);
    if (this.pendingInputRemovals.has(key) || this.state.pendingInputRemovals[key]) return;
    this.pendingInputRemovals.add(key);
    this.setPendingInputRemoval(key, {
      sessionId: id,
      executionId,
      submissionId,
      status: "removing",
    });
    try {
      await this.client.removePendingInput(id, executionId, submissionId);
    } catch {
      // Reconcile the latest snapshot before offering another removal attempt.
    }
    this.setPendingInputRemoval(key, {
      sessionId: id,
      executionId,
      submissionId,
      status: "uncertain",
    });
    try {
      await this.readPendingInputSnapshot(id, key);
    } catch {
      // Keep the action disabled until a later snapshot read resolves its state.
    } finally {
      this.pendingInputRemovals.delete(key);
      this.refreshSnapshot();
    }
  }

  async checkPendingInputRemoval(
    id: string,
    executionId: string,
    submissionId: string,
  ): Promise<void> {
    const key = pendingInputRemovalKey(id, executionId, submissionId);
    const current = this.state.pendingInputRemovals[key];
    if (current?.status !== "uncertain" || this.pendingInputRemovals.has(key)) return;
    this.pendingInputRemovals.add(key);
    this.setPendingInputRemoval(key, { ...current, status: "checking" });
    try {
      await this.readPendingInputSnapshot(id, key);
    } catch {
      this.setPendingInputRemoval(key, { ...current, status: "uncertain" });
    } finally {
      this.pendingInputRemovals.delete(key);
      this.refreshSnapshot();
    }
  }

  private async readPendingInputSnapshot(id: string, key: string): Promise<void> {
    const knownWhenRead = this.access.read().chats[id]?.knownExecutionId ?? null;
    const snapshot = await this.client.getSnapshot(id);
    if (
      snapshot.sessionId !== id ||
      !Number.isSafeInteger(snapshot.historyRevision) ||
      typeof snapshot.canChangeWorkingDirectory !== "boolean"
    )
      throw new TypeError("Invalid session snapshot after pending-input removal.");
    this.observeSnapshot(snapshot, knownWhenRead);
    // The view's snapshot owner refreshes after this read; publishing it here
    // could overwrite a newer view or a receipt accepted while the GET was in flight.
    this.setPendingInputRemoval(key, null);
  }

  retryOriginalSubmission(id: string): Promise<UserMessageReceipt | null> {
    return this.reconcile(id, true);
  }

  private async reconcileContinuation(
    id: string,
    retry: boolean,
  ): Promise<ContinuationReceipt | null> {
    const original = this.access.read().chats[id]?.pendingContinuation?.original;
    if (
      !original ||
      this.continuationChecks.has(id) ||
      (retry && (!this.access.canPersist() || this.executionSettingsSaving(id))) ||
      (!retry && document.hidden)
    )
      return null;

    const controller = retry ? null : new AbortController();
    if (controller) this.continuationReceiptReads.set(id, controller);
    this.continuationChecks.add(id);
    this.setCheckingContinuation(id, true);
    try {
      if (
        retry &&
        !this.access.updateChat(id, (previous) =>
          previous.pendingContinuation?.original.operationId === original.operationId
            ? {
                ...previous,
                pendingContinuation: { ...previous.pendingContinuation, status: "sending" },
                error: null,
              }
            : previous,
        )
      )
        throw new Error("The original Continue could not be journaled; it was not dispatched.");
      const receipt = retry
        ? await this.client.continueSession(id, original)
        : await this.client.getContinuationReceipt(id, original.operationId, controller?.signal);
      if (receipt === null) throw new Error("No receipt found. Continue remains uncertain.");
      this.resolveContinuation(id, original, receipt);
      return receipt;
    } catch (cause) {
      if (controller?.signal.aborted) return null;
      if (retry && cause instanceof RequestError && cause.status === 409)
        return await this.resolveContinuationConflict(id, original, cause);
      this.markContinuationUncertain(id, original, description(cause));
      return null;
    } finally {
      this.continuationChecks.delete(id);
      this.continuationReceiptReads.delete(id);
      this.setCheckingContinuation(id, false);
      this.refreshSnapshot();
    }
  }

  private async resolveContinuationConflict(
    id: string,
    original: ContinuationSubmission,
    conflict: RequestError,
  ): Promise<ContinuationReceipt | null> {
    try {
      const receipt = await this.client.getContinuationReceipt(id, original.operationId);
      if (receipt) {
        this.resolveContinuation(id, original, receipt);
        return receipt;
      }
      this.access.updateChat(id, (previous) =>
        previous.pendingContinuation?.original.operationId === original.operationId
          ? {
              ...previous,
              pendingContinuation: null,
              error: `Continue was rejected: ${requestErrorDescription(conflict)}`,
            }
          : previous,
      );
    } catch (cause) {
      this.markContinuationUncertain(
        id,
        original,
        `Continue outcome is uncertain: ${description(cause)}`,
      );
    }
    return null;
  }

  private resolveContinuation(
    id: string,
    original: ContinuationSubmission,
    receipt: ContinuationReceipt,
  ): void {
    this.access.updateChat(id, (previous) =>
      previous.pendingContinuation?.original.operationId === original.operationId
        ? applyContinuationReceipt(previous, id, receipt)
        : previous,
    );
  }

  private markContinuationUncertain(
    id: string,
    original: ContinuationSubmission,
    error: string,
  ): void {
    this.access.updateChat(id, (previous) =>
      previous.pendingContinuation?.original.operationId === original.operationId
        ? {
            ...previous,
            pendingContinuation: { ...previous.pendingContinuation, status: "uncertain" },
            error,
          }
        : previous,
    );
  }

  private async reconcile(id: string, retry: boolean): Promise<UserMessageReceipt | null> {
    const original = this.access.read().chats[id]?.pending?.original;
    if (
      !original ||
      this.checks.has(id) ||
      (retry && (!this.access.canPersist() || this.executionSettingsSaving(id))) ||
      (!retry && document.hidden)
    )
      return null;

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
      return receipt;
    } catch (cause) {
      if (controller?.signal.aborted) return null;
      this.access.updateChat(id, (previous) =>
        previous.pending
          ? {
              ...previous,
              pending: { ...previous.pending, status: "uncertain" },
              error: description(cause),
            }
          : previous,
      );
      return null;
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
    const compaction = this.state.compactions[snapshot.sessionId];
    if (
      (compaction?.phase === "observing" || compaction?.phase === "uncertain") &&
      snapshot.unsettledExecutionId !== compaction.operationId
    )
      void this.checkCompaction(snapshot.sessionId, compaction.operationId);

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
