import { RequestError } from "../http/RequestError.ts";
import { errorMessage, requestErrorDescription } from "../http/ErrorMessages.ts";
import { newUuid } from "../Uuid.ts";
import { maxUserImages } from "../chat/user-images/ImagePaste.ts";
import {
  applyContinuationReceipt,
  applyReceipt,
  beginContinuation,
  beginSubmission,
  idleChatSelection,
} from "../chat/ChatState.ts";
import type { LlmSelection } from "../providers/LlmSelection.ts";
import { submissionTextError } from "../sessions/inputs/UserMessageSubmission.ts";
import type { SessionClient } from "../sessions/SessionClient.ts";
import type { SessionSnapshot } from "../sessions/SessionSnapshot.ts";
import type {
  ContinuationReceipt,
  ContinuationSubmission,
} from "../sessions/inputs/Continuation.ts";
import type {
  UserMessageReceipt,
  UserMessageSubmission,
} from "../sessions/inputs/UserMessageSubmission.ts";
import type { WorkspaceCommandAccess, NotSentResendResult } from "./WorkspaceCommands.ts";

type SubmissionClient = Pick<
  SessionClient,
  "submitMessage" | "getMessageReceipt" | "continueSession" | "getContinuationReceipt"
>;
type SubmissionCoordinator = {
  executionSettingsSaving(id: string): boolean;
  isCompacting(id: string): boolean;
  isStopping(id: string): boolean;
  hasPendingInputRemoval(id: string): boolean;
  isChecking(id: string): boolean;
  isCheckingContinuation(id: string): boolean;
  beginReceiptRead(id: string, controller: AbortController | null): void;
  endReceiptRead(id: string): void;
  beginContinuationReceiptRead(id: string, controller: AbortController | null): void;
  endContinuationReceiptRead(id: string): void;
  refreshSnapshot(): void;
};

// The coordinator owns admission state, publication and receipt-read cancellation.
export class SubmissionCommands {
  private readonly client: SubmissionClient;
  private readonly access: WorkspaceCommandAccess;
  private readonly coordinator: SubmissionCoordinator;

  constructor(
    client: SubmissionClient,
    access: WorkspaceCommandAccess,
    coordinator: SubmissionCoordinator,
  ) {
    this.client = client;
    this.access = access;
    this.coordinator = coordinator;
  }

  async send(
    id: string,
    snapshot: SessionSnapshot,
    active: boolean,
    canSend: boolean,
    defaultSelection: LlmSelection | null,
  ): Promise<void> {
    if (
      !canSend ||
      !this.access.canPersist() ||
      this.coordinator.isCompacting(id) ||
      this.coordinator.executionSettingsSaving(id)
    )
      return;
    const latest = this.access.read().chats[id];
    const selection = active
      ? snapshot.latestLlmSelection
      : idleChatSelection(latest, snapshot, defaultSelection);
    if (
      !latest ||
      latest.pending ||
      latest.pendingContinuation ||
      latest.messageEdit ||
      !selection ||
      (!latest.draft.trim() && latest.draftImages.length === 0) ||
      latest.draftImages.length > maxUserImages ||
      (latest.draftImages.length > 0 && selection.supportsImages === false) ||
      submissionTextError(latest.draft)
    )
      return;

    const original: UserMessageSubmission = {
      submissionId: newUuid(),
      expectedHistoryRevision: snapshot.historyRevision,
      text: latest.draft,
      ...(latest.draftImages.length
        ? { imageIds: latest.draftImages.map((image) => image.imageId) }
        : {}),
      llmSelection: selection,
    };
    const persisted = this.access.updateChat(
      id,
      (previous) => beginSubmission(previous, original),
      active ? undefined : selection,
    );
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
    await this.reconcile(id, true, true);
  }

  async continueSession(
    id: string,
    snapshot: SessionSnapshot,
    canContinue: boolean,
    defaultSelection: LlmSelection | null,
  ): Promise<void> {
    const latest = this.access.read().chats[id];
    const selection = idleChatSelection(latest, snapshot, defaultSelection);
    const expectedLastExecutionId = snapshot.lastSettlement?.executionId;
    if (
      !canContinue ||
      this.coordinator.executionSettingsSaving(id) ||
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
      latest.draftImages.length > 0 ||
      latest.knownExecutionId ||
      this.coordinator.isStopping(id) ||
      this.coordinator.hasPendingInputRemoval(id) ||
      this.coordinator.isCompacting(id)
    )
      return;

    const original: ContinuationSubmission = {
      operationId: newUuid(),
      expectedHistoryRevision: snapshot.historyRevision,
      expectedLastExecutionId,
      llmSelection: structuredClone(selection),
    };
    if (
      !this.access.updateChat(id, (previous) => beginContinuation(previous, original), selection)
    ) {
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

  async resendNotSent(
    id: string,
    snapshot: SessionSnapshot,
    source: UserMessageSubmission,
  ): Promise<NotSentResendResult | null> {
    const latest = this.access.read().chats[id];
    if (
      snapshot.sessionId !== id ||
      (source.imageIds?.length ?? 0) > maxUserImages ||
      this.coordinator.executionSettingsSaving(id) ||
      this.coordinator.isCompacting(id) ||
      !latest ||
      latest.pending ||
      latest.pendingContinuation ||
      latest.messageEdit ||
      !this.access.canPersist()
    )
      return null;

    const submission: UserMessageSubmission = {
      submissionId: newUuid(),
      expectedHistoryRevision: snapshot.historyRevision,
      text: source.text,
      ...(source.imageIds ? { imageIds: [...source.imageIds] } : {}),
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
    const receipt = await this.reconcile(id, true, true);
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
      this.coordinator.isCheckingContinuation(id) ||
      (retry && (!this.access.canPersist() || this.coordinator.executionSettingsSaving(id))) ||
      (!retry && document.hidden)
    )
      return null;

    const controller = retry ? null : new AbortController();
    this.coordinator.beginContinuationReceiptRead(id, controller);
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
      this.markContinuationUncertain(id, original, errorMessage(cause));
      return null;
    } finally {
      this.coordinator.endContinuationReceiptRead(id);
      this.coordinator.refreshSnapshot();
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
        `Continue outcome is uncertain: ${errorMessage(cause)}`,
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

  private async reconcile(
    id: string,
    retry: boolean,
    firstAttempt = false,
  ): Promise<UserMessageReceipt | null> {
    const original = this.access.read().chats[id]?.pending?.original;
    if (
      !original ||
      this.coordinator.isChecking(id) ||
      (retry && (!this.access.canPersist() || this.coordinator.executionSettingsSaving(id))) ||
      (!retry && document.hidden)
    )
      return null;

    const controller = retry ? null : new AbortController();
    this.coordinator.beginReceiptRead(id, controller);
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
      if (retry && cause instanceof RequestError && cause.status === 400) {
        if (firstAttempt) {
          this.access.updateChat(id, (previous) =>
            previous.pending?.original.submissionId === original.submissionId
              ? {
                  ...previous,
                  pending: null,
                  error: `Send was rejected: ${requestErrorDescription(cause)}`,
                }
              : previous,
          );
          return null;
        }
        try {
          const receipt = await this.client.getMessageReceipt(id, original.submissionId);
          if (receipt) {
            this.access.updateChat(id, (previous) => applyReceipt(previous, id, receipt));
            return receipt;
          }
        } catch {
          // A failed receipt read cannot establish whether the submission was admitted.
        }
      }
      this.access.updateChat(id, (previous) =>
        previous.pending?.original.submissionId === original.submissionId
          ? {
              ...previous,
              pending: { ...previous.pending, status: "uncertain" },
              error: errorMessage(cause),
            }
          : previous,
      );
      return null;
    } finally {
      this.coordinator.endReceiptRead(id);
      this.coordinator.refreshSnapshot();
    }
  }
}
