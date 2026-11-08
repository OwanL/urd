import type { TargetedKeyboardEvent } from "preact";
import { useEffect, useLayoutEffect, useRef } from "preact/hooks";
import type { ComposerProps } from "./ComposerModels.ts";
import { ModelPicker } from "./ModelPicker.tsx";
import "./composer.css";

function effortLabel(effort: string): string {
  switch (effort) {
    case "none":
      return "None";
    case "low":
      return "Low";
    case "medium":
      return "Medium";
    case "high":
      return "High";
    case "extraHigh":
      return "Extra high";
    default:
      return effort;
  }
}

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
  const efforts = [...new Set(reasoningOptions)];
  const unknownEffort = reasoningEffort !== null && !efforts.includes(reasoningEffort);

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
          <select
            class="composer-selector composer-reasoning"
            aria-label="Reasoning effort"
            title={`Reasoning: ${reasoningEffort === null ? "Provider default" : effortLabel(reasoningEffort)}`}
            disabled={locked}
            value={reasoningEffort === null ? "default" : `effort:${reasoningEffort}`}
            onChange={(event) => {
              const value = event.currentTarget.value;
              onReasoningChange(value === "default" ? null : value.slice("effort:".length));
            }}
          >
            <option value="default">Default</option>
            {unknownEffort && (
              <option value={`effort:${reasoningEffort}`} disabled>
                {effortLabel(reasoningEffort)}
              </option>
            )}
            {efforts.map((effort) => (
              <option key={effort} value={`effort:${effort}`}>
                {effortLabel(effort)}
              </option>
            ))}
          </select>
        </div>
        <div class="composer-actions">
          {(working || stopping) && (
            <button
              class="composer-stop"
              type="button"
              disabled={stopping}
              aria-busy={stopping}
              onClick={onStop}
            >
              {stopping ? "Stopping…" : "Stop"}
            </button>
          )}
          <button class="composer-send" type="submit" disabled={!canSend}>
            Send
          </button>
        </div>
      </div>
    </form>
  );
}
