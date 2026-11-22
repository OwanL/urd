import { useId, useLayoutEffect, useRef, useState } from "preact/hooks";
import { Icon } from "../../controls/Icon.tsx";
import { IconButton } from "../../controls/IconButton.tsx";
import type { UserQuestion } from "../../sessions/inputs/UserQuestion.ts";

interface QuestionPromptProps {
  question: UserQuestion;
  draft: string;
  disabled: boolean;
  onDraft: (text: string) => void;
  onAnswer: (answer: string) => void;
}

export function QuestionPrompt({
  question,
  draft,
  disabled,
  onDraft,
  onAnswer,
}: QuestionPromptProps) {
  const [editing, setEditing] = useState(Boolean(draft));
  const customButton = useRef<HTMLButtonElement>(null);
  const field = useRef<HTMLTextAreaElement>(null);
  const focusEditor = useRef(false);
  const composing = useRef(false);
  const questionId = useId();
  const editorId = useId();
  const hasChoices = question.answers.length > 0;
  const context = question.context?.trim();
  const showEditor = !hasChoices || editing;
  const canSubmit = !disabled && Boolean(draft.trim());

  function submitDraft() {
    if (canSubmit) onAnswer(draft);
  }

  useLayoutEffect(() => {
    if (editing && focusEditor.current) field.current?.focus();
    focusEditor.current = false;
  }, [editing]);

  return (
    <>
      <div class="user-question-heading">
        <div class="user-question-title">
          <h2 id={questionId}>{question.question}</h2>
          {question.sourceTitle && (
            <span class="user-question-source">
              <Icon name="subagents" />
              <span>{question.sourceTitle}</span>
            </span>
          )}
        </div>
        {context && <p class="user-question-context">{context}</p>}
      </div>
      {hasChoices && (
        <div class="user-question-options" role="group" aria-labelledby={questionId}>
          {question.answers.map((option) => (
            <button
              class="user-question-choice"
              key={option}
              type="button"
              disabled={disabled}
              onClick={() => onAnswer(option)}
            >
              {option}
            </button>
          ))}
          <button
            ref={customButton}
            class="user-question-custom"
            type="button"
            aria-expanded={editing}
            aria-controls={editing ? editorId : undefined}
            onClick={() => {
              focusEditor.current = !editing;
              setEditing(!editing);
            }}
          >
            <Icon name={editing ? "close" : "edit"} />
            {editing ? "Hide reply" : "Write a reply"}
          </button>
        </div>
      )}
      {showEditor && (
        <form
          id={editorId}
          class="user-question-form"
          onSubmit={(event) => {
            event.preventDefault();
            submitDraft();
          }}
        >
          <textarea
            ref={field}
            aria-label="Reply"
            aria-describedby={questionId}
            placeholder="Type a reply"
            rows={1}
            maxLength={8000}
            value={draft}
            onInput={(event) => onDraft(event.currentTarget.value)}
            onCompositionStart={() => {
              composing.current = true;
            }}
            onCompositionEnd={() => {
              composing.current = false;
            }}
            onKeyDown={(event) => {
              if (event.isComposing || event.keyCode === 229 || composing.current) return;
              if (event.key === "Enter" && !event.shiftKey) {
                event.preventDefault();
                submitDraft();
              } else if (event.key === "Escape" && hasChoices) {
                event.stopPropagation();
                setEditing(false);
                customButton.current?.focus();
              }
            }}
          />
          <IconButton
            class="user-question-send"
            type="submit"
            icon="send"
            label="Send reply"
            hint={
              <>
                Send reply <kbd>Enter</kbd>
              </>
            }
            disabled={!canSubmit}
          />
        </form>
      )}
    </>
  );
}
