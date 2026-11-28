import "../../controls/selector.css";
import type { ComponentChildren, RefObject, TargetedKeyboardEvent } from "preact";
import { useCallback, useEffect, useLayoutEffect, useRef } from "preact/hooks";
import { Icon } from "../../controls/Icon.tsx";
import { IconButton } from "../../controls/IconButton.tsx";
import { composerSubmissionState } from "./ComposerSubmission.ts";
import { ModelPicker } from "../../providers/models/ModelPicker.tsx";
import type { ModelPickerOption } from "../../providers/models/ModelPicker.tsx";
import { ComposerControls } from "./ComposerControls.tsx";
import { ReasoningPicker } from "./ReasoningPicker.tsx";

// Native content sizing removes the per-keystroke measure-and-mutate cycle;
// browsers without support keep the JS resize below.
const fieldSizing = CSS.supports("field-sizing", "content");

interface ComposerProps {
  input: RefObject<HTMLTextAreaElement | null>;
  text: string;
  hasImages: boolean;
  imagePreviews: ComponentChildren;
  onTextChange: (text: string) => void;
  models: readonly ModelPickerOption[];
  modelKey: string | null;
  modelLabel: string;
  onModelChange: (key: string) => void;
  hasProviders: boolean | null;
  loadingModels: boolean;
  refreshingModels: boolean;
  modelsError: string | null;
  onRetryModels: () => void;
  onSetUpProvider: () => void;
  reasoningEffort: string | null;
  reasoningOptions: readonly string[];
  onReasoningChange: (effort: string | null) => void;
  selectionLocked: boolean;
  configurationPending: boolean;
  canSend: boolean;
  canContinue: boolean;
  continuationAvailable: boolean;
  working: boolean;
  stopping: boolean;
  onSend: () => void;
  onContinue: () => void;
  onStop: () => void;
  browserLauncher?: ComponentChildren;
  subagentProviders?: ComponentChildren;
  indicators?: ComponentChildren;
}

function resizeInput(input: HTMLTextAreaElement) {
  input.style.height = "0px";
  input.style.height = `${input.scrollHeight}px`;
}

export function Composer({
  input,
  text,
  hasImages,
  imagePreviews,
  onTextChange,
  models,
  modelKey,
  modelLabel,
  onModelChange,
  hasProviders,
  loadingModels,
  refreshingModels,
  modelsError,
  onRetryModels,
  onSetUpProvider,
  reasoningEffort,
  reasoningOptions,
  onReasoningChange,
  selectionLocked,
  configurationPending,
  canSend,
  canContinue,
  continuationAvailable,
  working,
  stopping,
  onSend,
  onContinue,
  onStop,
  browserLauncher,
  subagentProviders,
  indicators,
}: ComposerProps) {
  const focusInput = useCallback(() => input.current?.focus({ preventScroll: true }), [input]);
  const composing = useRef(false);
  const locked = selectionLocked || working || stopping;
  const submission = composerSubmissionState(
    text,
    canSend,
    canContinue,
    continuationAvailable,
    hasImages,
  );
  const canSubmit = submission.action !== null;
  function submit() {
    if (submission.action === "send") onSend();
    else if (submission.action === "continue") onContinue();
  }

  useLayoutEffect(() => {
    const textarea = input.current;
    if (!textarea || fieldSizing) return;
    resizeInput(textarea);
  }, [input, text]);

  useEffect(() => {
    const textarea = input.current;
    if (!textarea || fieldSizing) return;
    let width = textarea.clientWidth;
    const observer = new ResizeObserver(() => {
      if (textarea.clientWidth === width) return;
      width = textarea.clientWidth;
      resizeInput(textarea);
    });
    observer.observe(textarea);
    return () => observer.disconnect();
  }, [input]);

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
      {browserLauncher && <div class="browser-launcher-slot">{browserLauncher}</div>}
      {imagePreviews}
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
      <ComposerControls
        selectionLayoutKey={`${modelLabel}:${reasoningEffort}:${hasProviders}`}
        focusFallback={focusInput}
        indicators={indicators}
        selections={
          <>
            {hasProviders === false ? (
              <button
                class="selector composer-add-provider"
                type="button"
                disabled={locked}
                onClick={onSetUpProvider}
              >
                <Icon name="plus" />
                <span>Add provider</span>
              </button>
            ) : (
              <ModelPicker
                models={models}
                modelKey={modelKey}
                modelLabel={modelLabel}
                onModelChange={onModelChange}
                loadingModels={loadingModels}
                refreshingModels={refreshingModels}
                modelsError={modelsError}
                onRetryModels={onRetryModels}
                disabled={locked}
              />
            )}
            {hasProviders !== false && (
              <ReasoningPicker
                effort={reasoningEffort}
                options={reasoningOptions}
                onChange={onReasoningChange}
                disabled={locked || !modelKey}
              />
            )}
            {subagentProviders}
          </>
        }
        actions={
          <>
            {working ? (
              <IconButton
                icon="stop"
                label="Stop"
                hint={stopping ? "Stopping…" : "Stop"}
                class="composer-stop"
                disabled={stopping}
                aria-busy={stopping}
                onClick={onStop}
              />
            ) : null}
            <IconButton
              icon="send"
              label={submission.label}
              hint={
                configurationPending ? (
                  "Saving model settings…"
                ) : (
                  <>
                    {submission.label} <kbd>Enter</kbd>
                  </>
                )
              }
              class="composer-send"
              aria-busy={configurationPending}
              type="submit"
              disabled={!canSubmit}
            />
          </>
        }
      />
    </form>
  );
}
