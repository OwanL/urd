import { createContext } from "preact";
import type { ComponentChildren } from "preact";
import { useContext, useLayoutEffect, useRef } from "preact/hooks";
import type { LlmSelection } from "../providers/LlmSelection.ts";
import type { MessageEditDraft } from "../sessions/MessageEdit.ts";
import type { TranscriptEntryRef, TranscriptTextFacts } from "../sessions/Messages.ts";
import type { MessageEditing } from "./MessageEditing.ts";
import { submissionTextError } from "./WorkspaceState.ts";
import "./message-editing.css";

export interface MessageEditingContextValue {
  controller: MessageEditing;
  sessionId: string;
  draft: MessageEditDraft | undefined;
  selection: LlmSelection | null;
  canEdit: boolean;
}

export const MessageEditingContext = createContext<MessageEditingContextValue | null>(null);

export function EditableMessage({
  entry,
  text,
  children,
}: {
  entry: TranscriptEntryRef;
  text: TranscriptTextFacts;
  children: ComponentChildren;
}) {
  const context = useContext(MessageEditingContext);
  const shell = useRef<HTMLDivElement>(null);
  const button = useRef<HTMLButtonElement>(null);
  const height = useRef<number | undefined>(undefined);
  const pointer = useRef<{ x: number; y: number } | null>(null);
  const target = context?.draft?.target;
  const editing =
    target?.executionId === entry.executionId &&
    target.entryId === entry.entryId &&
    target.historyRevision === entry.historyRevision;
  if (!context || entry.kind !== "user") return <>{children}</>;
  const begin = () => {
    if (!context.canEdit || context.draft) return;
    height.current = shell.current?.getBoundingClientRect().height;
    void context.controller.open(context.sessionId, entry, text);
  };
  return (
    <div
      ref={shell}
      class="editable-message"
      style={editing && height.current ? { minHeight: `${height.current}px` } : undefined}
      onPointerDown={(event) => {
        pointer.current = { x: event.clientX, y: event.clientY };
      }}
      onClick={(event) => {
        if (
          editing ||
          event.detail !== 1 ||
          !(event.target instanceof Element) ||
          !event.target.closest(".message-user") ||
          event.target.closest(
            'a, button, input, textarea, select, summary, [role="button"], [contenteditable="true"]',
          ) ||
          !window.getSelection()?.isCollapsed
        )
          return;
        const start = pointer.current;
        if (start && Math.hypot(event.clientX - start.x, event.clientY - start.y) < 5) begin();
      }}
    >
      {editing && context.draft ? (
        <InlineMessageEditor
          context={context}
          draft={context.draft}
          onCancel={() => {
            context.controller.cancel(context.sessionId);
            requestAnimationFrame(() => button.current?.focus({ preventScroll: true }));
          }}
        />
      ) : (
        <>
          {children}
          <button
            ref={button}
            class="message-edit-action"
            type="button"
            disabled={!context.canEdit || Boolean(context.draft)}
            onClick={begin}
          >
            Edit message
          </button>
        </>
      )}
    </div>
  );
}

export function InlineMessageEditor({
  context,
  draft,
  onCancel,
}: {
  context: MessageEditingContextValue;
  draft: MessageEditDraft;
  onCancel: () => void;
}) {
  const input = useRef<HTMLTextAreaElement>(null);
  const editor = useRef<HTMLDivElement>(null);
  const editing = draft.status === "editing";
  const textError = submissionTextError(draft.text);
  useLayoutEffect(() => editor.current?.focus({ preventScroll: true }), []);
  useLayoutEffect(() => {
    if (!editing || !input.current) return;
    input.current.focus({ preventScroll: true });
    input.current.setSelectionRange(input.current.value.length, input.current.value.length);
  }, [editing]);
  const save = () => {
    const element = editor.current;
    const panel = element?.closest<HTMLElement>("[data-chat-session]");
    void context.controller.save(context.sessionId, context.selection).then(() => {
      requestAnimationFrame(() => {
        if (
          element &&
          !element.isConnected &&
          panel?.isConnected &&
          !panel.hidden &&
          document.activeElement === document.body
        )
          panel
            .querySelector<HTMLTextAreaElement>('textarea[aria-label="Message"]')
            ?.focus({ preventScroll: true });
      });
    });
  };
  return (
    <div
      ref={editor}
      tabIndex={-1}
      onKeyDown={(event) => {
        if (event.key === "Escape" && !event.isComposing && !draft.submission) {
          event.preventDefault();
          onCancel();
        }
      }}
      class="message message-user message-editor"
      data-message-editor=""
      aria-busy={draft.status === "saving"}
    >
      <textarea
        ref={input}
        aria-label="Edit message"
        value={draft.text}
        readOnly={!editing}
        onInput={(event) => context.controller.change(context.sessionId, event.currentTarget.value)}
        onKeyDown={(event) => {
          if (event.isComposing || event.keyCode === 229) return;
          if (event.key === "Enter" && !event.shiftKey) {
            event.preventDefault();
            save();
          }
        }}
      />
      <div class="message-editor-actions">
        <button type="button" disabled={Boolean(draft.submission)} onClick={onCancel}>
          Cancel
        </button>
        <button
          type="button"
          disabled={!editing || !draft.text.trim() || Boolean(textError) || !context.selection}
          onClick={save}
        >
          Save
        </button>
      </div>
      <div role="status">
        {draft.status === "loading"
          ? "Loading message…"
          : draft.status === "saving"
            ? "Stopping work and saving…"
            : null}
        {(draft.error || textError) && <span>{draft.error || textError}</span>}
      </div>
    </div>
  );
}
