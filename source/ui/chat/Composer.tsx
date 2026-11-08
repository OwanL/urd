import type { TargetedKeyboardEvent } from "preact";
import { useEffect, useLayoutEffect, useRef } from "preact/hooks";
import { IconButton } from "../components/IconButton.tsx";
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
  working,
  stopping,
  onSend,
  onStop,
}: ComposerProps) {
  const input = useRef<HTMLTextAreaElement>(null);
  const composing = useRef(false);
  const locked = selectionLocked || working || stopping;

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
    if (canSend) onSend();
  }

  return (
    <form
      class="composer"
      aria-label="Message composer"
      onSubmit={(event) => {
        event.preventDefault();
        if (canSend) onSend();
      }}
    >
      <textarea
        class="composer-input"
        ref={input}
        aria-label="Message"
        placeholder="Message"
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
          {(working || stopping) && (
            <IconButton
              icon="stop"
              label="Stop"
              hint={stopping ? "Stopping…" : "Stop"}
              class="composer-stop"
              disabled={stopping}
              aria-busy={stopping}
              onClick={onStop}
            />
          )}
          <IconButton
            icon="send"
            label="Send"
            hint={
              <>
                Send <kbd>Enter</kbd>
              </>
            }
            class="composer-send"
            type="submit"
            disabled={!canSend}
          />
        </div>
      </div>
    </form>
  );
}
