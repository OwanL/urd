import type { TargetedKeyboardEvent } from "preact";
import { useEffect, useId, useLayoutEffect, useRef, useState } from "preact/hooks";
import { IconButton } from "../components/IconButton.tsx";
import { Popover } from "../components/Popover.tsx";
import { composerSubmissionState } from "./ComposerSubmission.ts";
import type { ComposerProps } from "./ComposerTypes.ts";
import { ModelPicker } from "./ModelPicker.tsx";
import "./composer.css";
import { ReasoningPicker } from "./ReasoningPicker.tsx";

function resizeInput(input: HTMLTextAreaElement) {
  input.style.height = "0px";
  input.style.height = `${input.scrollHeight}px`;
}

export function Composer({
  text,
  onTextChange,
  models,
  modelKey,
  modelLabel,
  onModelChange,
  onRefreshModels,
  refreshingModels,
  modelsError,
  reasoningEffort,
  reasoningOptions,
  onReasoningChange,
  selectionLocked,
  canSend,
  canContinue,
  continuationAvailable,
  working,
  stopping,
  onSend,
  onContinue,
  onStop,
  indicators,
}: ComposerProps) {
  const input = useRef<HTMLTextAreaElement>(null);
  const overflowTrigger = useRef<HTMLButtonElement>(null);
  const overflowId = useId();
  const [overflowOpen, setOverflowOpen] = useState(false);
  const composing = useRef(false);
  const locked = selectionLocked || working || stopping;
  const submission = composerSubmissionState(text, canSend, canContinue, continuationAvailable);
  const canSubmit = submission.action !== null;
  function submit() {
    if (submission.action === "send") onSend();
    else if (submission.action === "continue") onContinue();
  }

  useLayoutEffect(() => {
    const textarea = input.current;
    if (!textarea) return;
    resizeInput(textarea);
  }, [text]);

  useEffect(() => {
    const textarea = input.current;
    if (!textarea) return;
    let width = textarea.clientWidth;
    const observer = new ResizeObserver(() => {
      if (textarea.clientWidth === width) return;
      width = textarea.clientWidth;
      resizeInput(textarea);
    });
    observer.observe(textarea);
    return () => observer.disconnect();
  }, []);

  function send(event: TargetedKeyboardEvent<HTMLTextAreaElement>) {
    if (
      event.key !== "Enter" ||
      event.shiftKey ||
      event.isComposing ||
      event.keyCode === 229 ||
      composing.current
    )
      return;
    event.preventDefault();
    if (canSubmit) submit();
  }

  return (
    <form
      class="composer"
      aria-label="Message composer"
      onSubmit={(event) => {
        event.preventDefault();
        if (canSubmit) submit();
      }}
    >
      <textarea
        class="composer-input"
        ref={input}
        aria-label="Message"
        placeholder="Ask Urd"
        rows={1}
        value={text}
        onInput={(event) => onTextChange(event.currentTarget.value)}
        onKeyDown={send}
        onCompositionStart={() => {
          composing.current = true;
        }}
        onCompositionEnd={() => {
          composing.current = false;
        }}
      />
      <div class="composer-controls">
        <div class="composer-selections">
          <ModelPicker
            models={models}
            modelKey={modelKey}
            modelLabel={modelLabel}
            onModelChange={onModelChange}
            onRefreshModels={onRefreshModels}
            refreshingModels={refreshingModels}
            modelsError={modelsError}
            disabled={locked}
          />
          <ReasoningPicker
            effort={reasoningEffort}
            options={reasoningOptions}
            onChange={onReasoningChange}
            disabled={locked || !modelKey}
          />
        </div>
        <div class="composer-actions">
          <div class="composer-overflow">
            <IconButton
              icon="more"
              label="More options"
              hint="Session details"
              buttonRef={overflowTrigger}
              aria-expanded={overflowOpen}
              aria-controls={overflowOpen ? overflowId : undefined}
              aria-haspopup="dialog"
              onClick={() => setOverflowOpen((open) => !open)}
            />
            {overflowOpen && (
              <Popover
                id={overflowId}
                anchor={overflowTrigger}
                label="Session details"
                class="composer-overflow-popover"
                placement="above"
                onClose={() => setOverflowOpen(false)}
              >
                {indicators}
              </Popover>
            )}
          </div>
          <IconButton
            icon="stop"
            label="Stop"
            hint={stopping ? "Stopping…" : "Stop"}
            class="composer-stop"
            disabled={!working || stopping}
            aria-busy={stopping}
            onClick={onStop}
          />
          <IconButton
            icon="send"
            label={submission.label}
            hint={
              <>
                {submission.label} <kbd>Enter</kbd>
              </>
            }
            class="composer-send"
            type="submit"
            disabled={!canSubmit}
          />
        </div>
      </div>
    </form>
  );
}
