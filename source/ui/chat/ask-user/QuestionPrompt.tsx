import { useId, useLayoutEffect, useRef, useState } from "preact/hooks";
import { Icon } from "../../controls/Icon.tsx";
import { IconButton } from "../../controls/IconButton.tsx";
import { Popover } from "../../controls/Popover.tsx";
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
  const [showContext, setShowContext] = useState(false);
  const contextButton = useRef<HTMLButtonElement>(null);
  const customButton = useRef<HTMLButtonElement>(null);
  const field = useRef<HTMLTextAreaElement>(null);
  const focusEditor = useRef(false);
  const contextId = useId();
  const editorId = useId();
  const hasChoices = question.answers.length > 0;
  const hasContext = Boolean(question.context?.trim() || question.sourceTitle);
  const showEditor = !hasChoices || editing;

  useLayoutEffect(() => {
    if (editing && focusEditor.current) field.current?.focus({ preventScroll: true });
    focusEditor.current = false;
  }, [editing]);

  return (
    <>
      <div class="user-question-line">
        <div class="user-question-heading">
          <span class="user-question-text">{question.question}</span>
          {hasContext && (
            <button
              ref={contextButton}
              class="user-question-context-button"
              type="button"
              aria-haspopup="dialog"
              aria-expanded={showContext}
              aria-controls={showContext ? contextId : undefined}
              onClick={() => setShowContext((open) => !open)}
            >
              Context
            </button>
          )}
        </div>
        {hasChoices && (
          <div class="user-question-options">
            {question.answers.map((option) => (
              <button
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
              class="user-question-other"
              type="button"
              aria-label="Other answer"
              aria-expanded={editing}
              aria-controls={editing ? editorId : undefined}
              onClick={() => {
                focusEditor.current = !editing;
                setEditing(!editing);
              }}
            >
              Other…
            </button>
          </div>
        )}
      </div>
      {showEditor && (
        <form
          id={editorId}
          class="user-question-form"
          onSubmit={(event) => {
            event.preventDefault();
            onAnswer(draft);
          }}
        >
          <textarea
            ref={field}
            aria-label="Custom answer"
            placeholder="Custom answer"
            rows={1}
            maxLength={8000}
            value={draft}
            onInput={(event) => onDraft(event.currentTarget.value)}
            onKeyDown={(event) => {
              if (event.key !== "Escape" || event.isComposing || !hasChoices) return;
              event.stopPropagation();
              setEditing(false);
              customButton.current?.focus({ preventScroll: true });
            }}
          />
          <IconButton
            type="submit"
            icon="send"
            label="Submit answer"
            disabled={disabled || !draft.trim()}
          />
        </form>
      )}
      {showContext && (
        <Popover
          anchor={contextButton}
          id={contextId}
          class="user-question-context"
          label="Question context"
          placement="above"
          onClose={() => setShowContext(false)}
        >
          {question.sourceTitle && (
            <div class="user-question-source">
              <Icon name="subagents" />
              {question.sourceTitle}
            </div>
          )}
          {question.context}
        </Popover>
      )}
    </>
  );
}
