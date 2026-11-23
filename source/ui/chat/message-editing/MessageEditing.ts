import { RequestError } from "../../http/RequestError.ts";
import { newUuid } from "../../Uuid.ts";
import type { LlmSelection } from "../../providers/LlmSelection.ts";
import type {
  MessageEditReceipt,
  MessageEditSubmission,
} from "../../sessions/inputs/MessageEdit.ts";
import type {
  TranscriptEntryRef,
  TranscriptTextFacts,
  UserImage,
} from "../../sessions/transcript/Messages.ts";
import type { SessionClient } from "../../sessions/SessionClient.ts";
import type { MessageEditingAccess } from "./MessageEditingAccess.ts";
import { submissionTextError } from "../../sessions/inputs/UserMessageSubmission.ts";
import { isUserImage } from "../ChatState.ts";
import { maxUserImages } from "../user-images/ImagePaste.ts";

// Owns inline drafts and the exact Save journal independently of transcript row lifetimes.
export class MessageEditing {
  private readonly requests = new Set<string>();
  private readonly reads = new Map<string, AbortController>();

  private readonly client: Pick<
    SessionClient,
    "getMessageContent" | "editMessage" | "getMessageEditReceipt"
  >;
  private readonly access: MessageEditingAccess;
  private readonly refresh: () => void;

  constructor(
    client: Pick<SessionClient, "getMessageContent" | "editMessage" | "getMessageEditReceipt">,
    access: MessageEditingAccess,
    refresh: () => void,
  ) {
    this.client = client;
    this.access = access;
    this.refresh = refresh;
  }

  async open(
    id: string,
    target: TranscriptEntryRef,
    facts: TranscriptTextFacts | undefined,
    images: readonly UserImage[] = [],
  ): Promise<void> {
    const chat = this.access.read().chats[id];
    if (
      !chat ||
      chat.pending ||
      chat.pendingContinuation ||
      chat.messageEdit ||
      target.kind !== "user" ||
      !this.access.canPersist()
    )
      return;
    if (
      !this.access.updateChat(id, (previous) => ({
        ...previous,
        messageEdit: {
          target,
          text: "",
          images: [...images],
          status: facts ? "loading" : "editing",
          error: null,
        },
      }))
    )
      return;
    if (!facts) return;
    const controller = new AbortController();
    this.reads.set(id, controller);
    try {
      let cursor = facts.from;
      const pieces: string[] = [];
      while (true) {
        const page = await this.client.getMessageContent(id, cursor, controller.signal);
        controller.signal.throwIfAborted();
        if (
          page.from.segmentIndex !== cursor.segmentIndex ||
          page.from.utf8Offset !== cursor.utf8Offset ||
          page.from.message.executionId !== cursor.message.executionId ||
          page.from.message.messageId !== cursor.message.messageId ||
          page.from.message.role !== cursor.message.role ||
          page.from.message.historyRevision !== cursor.message.historyRevision
        )
          throw new Error("Message cursor did not match.");
        pieces.push(page.text);
        if (!page.hasMore) break;
        if (
          page.next.segmentIndex === cursor.segmentIndex &&
          page.next.utf8Offset === cursor.utf8Offset
        )
          throw new Error("Message content did not advance.");
        cursor = page.next;
      }
      this.access.updateChat(id, (previous) =>
        previous.messageEdit?.target === target
          ? {
              ...previous,
              messageEdit: {
                ...previous.messageEdit,
                text: pieces.join(""),
                status: "editing",
                error: null,
              },
            }
          : previous,
      );
    } catch (cause) {
      if (controller.signal.aborted) return;
      this.access.updateChat(id, (previous) =>
        previous.messageEdit?.target === target
          ? {
              ...previous,
              messageEdit: { ...previous.messageEdit, error: description(cause) },
            }
          : previous,
      );
    } finally {
      if (this.reads.get(id) === controller) this.reads.delete(id);
    }
  }

  change(id: string, text: string): void {
    this.access.updateChat(id, (chat) =>
      chat.messageEdit?.status === "editing"
        ? {
            ...chat,
            messageEdit: { ...chat.messageEdit, text, error: null },
          }
        : chat,
    );
  }

  appendImage(id: string, target: TranscriptEntryRef, image: UserImage): boolean {
    if (!isUserImage(image)) return false;
    let changed = false;
    this.access.updateChat(id, (chat) => {
      const edit = chat.messageEdit;
      if (
        edit?.target !== target ||
        edit.status !== "editing" ||
        edit.images?.some((item) => item.imageId === image.imageId)
      )
        return chat;
      changed = true;
      return {
        ...chat,
        messageEdit: { ...edit, images: [...(edit.images ?? []), image], error: null },
      };
    });
    return changed;
  }

  removeImage(id: string, target: TranscriptEntryRef, imageId: string): boolean {
    let changed = false;
    this.access.updateChat(id, (chat) => {
      const edit = chat.messageEdit;
      if (
        edit?.target !== target ||
        edit.status !== "editing" ||
        !edit.images?.some((image) => image.imageId === imageId)
      )
        return chat;
      changed = true;
      return {
        ...chat,
        messageEdit: {
          ...edit,
          images: edit.images.filter((image) => image.imageId !== imageId),
          error: null,
        },
      };
    });
    return changed;
  }

  cancel(id: string): void {
    this.reads.get(id)?.abort();
    this.access.updateChat(id, (chat) =>
      chat.messageEdit?.submission ? chat : { ...chat, messageEdit: undefined },
    );
  }

  async save(id: string, selection: LlmSelection | null): Promise<void> {
    const chat = this.access.read().chats[id];
    const edit = chat?.messageEdit;
    if (
      !edit ||
      edit.status !== "editing" ||
      (!edit.text.trim() && !edit.images?.length) ||
      (edit.images?.length ?? 0) > maxUserImages ||
      submissionTextError(edit.text) ||
      !selection ||
      (selection.supportsImages === false && !!edit.images?.length) ||
      chat.pending ||
      chat.pendingContinuation ||
      !this.access.canPersist()
    )
      return;
    const original: MessageEditSubmission = {
      operationId: newUuid(),
      message: {
        executionId: edit.target.executionId,
        messageId: edit.target.entryId,
        role: "user",
        historyRevision: edit.target.historyRevision,
      },
      text: edit.text,
      imageIds: (edit.images ?? []).map((image) => image.imageId),
      llmSelection: structuredClone(selection),
    };
    if (
      !this.access.updateChat(id, (previous) => ({
        ...previous,
        messageEdit: { ...edit, submission: original, status: "saving", error: null },
      }))
    ) {
      this.access.updateChat(id, (previous) => ({
        ...previous,
        messageEdit: {
          ...edit,
          error: "Save was not dispatched because its journal could not be saved.",
        },
      }));
      return;
    }
    await this.reconcile(id, true);
  }

  async reconcile(id: string, retry = false): Promise<void> {
    const edit = this.access.read().chats[id]?.messageEdit;
    const original = edit?.submission;
    if (!original || this.requests.has(id) || (retry && !this.access.canPersist())) return;
    this.requests.add(id);
    try {
      if (
        retry &&
        !this.access.updateChat(id, (chat) => ({
          ...chat,
          messageEdit: { ...edit, status: "saving", error: null },
        }))
      )
        throw new Error("Save was not dispatched because its journal could not be saved.");
      if (!retry)
        this.access.updateChat(id, (chat) =>
          chat.messageEdit
            ? {
                ...chat,
                messageEdit: { ...chat.messageEdit, error: "Checking receipt…" },
              }
            : chat,
        );
      const receipt = retry
        ? await this.client.editMessage(id, original)
        : await this.client.getMessageEditReceipt(id, original.operationId);
      if (receipt === null) throw new Error("No receipt found. Save remains uncertain.");
      this.applyReceipt(id, original, receipt);
    } catch (cause) {
      const rejected = cause instanceof RequestError && [400, 404, 409].includes(cause.status);
      this.access.updateChat(id, (chat) =>
        chat.messageEdit?.submission?.operationId === original.operationId
          ? {
              ...chat,
              messageEdit: {
                ...chat.messageEdit,
                submission: rejected ? undefined : original,
                status: rejected ? "editing" : "uncertain",
                error: rejected
                  ? "The edit was rejected. Review the conversation before trying again."
                  : description(cause),
              },
            }
          : chat,
      );
    } finally {
      this.requests.delete(id);
      this.refresh();
    }
  }

  watchPendingReceipts(): () => void {
    const check = () => {
      if (document.hidden) return;
      for (const [id, chat] of Object.entries(this.access.read().chats))
        if (chat.messageEdit?.status === "uncertain") void this.reconcile(id);
    };
    check();
    window.addEventListener("focus", check);
    document.addEventListener("visibilitychange", check);
    return () => {
      window.removeEventListener("focus", check);
      document.removeEventListener("visibilitychange", check);
    };
  }

  private applyReceipt(
    id: string,
    original: MessageEditSubmission,
    receipt: MessageEditReceipt,
  ): void {
    if (
      receipt.sessionId !== id ||
      receipt.operationId !== original.operationId ||
      receipt.historyRevision !== original.message.historyRevision + 1 ||
      !receipt.executionId
    )
      throw new Error("Edit receipt does not match the original Save.");
    this.access.updateChat(id, (chat) =>
      chat.messageEdit?.submission?.operationId === original.operationId
        ? {
            ...chat,
            messageEdit: undefined,
            knownExecutionId: receipt.executionId,
            error: null,
          }
        : chat,
    );
  }
}

function description(cause: unknown): string {
  return cause instanceof Error ? cause.message : String(cause);
}
