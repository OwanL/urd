import type { TargetedKeyboardEvent } from "preact";
import { useEffect, useId, useLayoutEffect, useRef, useState } from "preact/hooks";
import { Icon } from "../components/Icon.tsx";
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
  const controls = useRef<HTMLDivElement>(null);
  const selections = useRef<HTMLDivElement>(null);
  const actions = useRef<HTMLDivElement>(null);
  const accounting = useRef<HTMLDivElement>(null);
  const accountingWidth = useRef(0);
  const [inlineAccounting, setInlineAccounting] = useState(true);
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

  useLayoutEffect(() => {
    const row = controls.current;
    const pinned = selections.current;
    const buttons = actions.current;
    if (!row || !pinned || !buttons) return;
    function layout() {
      if (!row!.clientWidth) return;
      if (inlineAccounting && accounting.current)
        accountingWidth.current = accounting.current.getBoundingClientRect().width;
      const flex = pinned!.style.flex;
      pinned!.style.flex = "none";
      const pinnedWidth = pinned!.getBoundingClientRect().width;
      pinned!.style.flex = flex;
      const gap = Number.parseFloat(getComputedStyle(row!).columnGap) || 0;
      const actionGap = Number.parseFloat(getComputedStyle(buttons!).columnGap) || 0;
      const more = overflowTrigger.current?.getBoundingClientRect().width ?? 0;
      const actionWidth = buttons!.getBoundingClientRect().width - (more ? more + actionGap : 0);
      const next =
        pinnedWidth + accountingWidth.current + actionWidth + gap * 2 <= row!.clientWidth;
      if (next === inlineAccounting) return;
      const focused = document.activeElement;
      const group = accounting.current;
      const menu = document.getElementById(overflowId);
      if (
        focused === overflowTrigger.current ||
        group?.contains(focused) ||
        menu?.contains(focused) ||
        [...(group?.querySelectorAll("[aria-controls]") ?? [])].some((control) =>
          document.getElementById(control.getAttribute("aria-controls")!)?.contains(focused),
        )
      )
        input.current?.focus({ preventScroll: true });
      setOverflowOpen(false);
      setInlineAccounting(next);
    }
    const observer = new ResizeObserver(layout);
    for (const element of [row, pinned, buttons, inlineAccounting ? accounting.current : null])
      if (element) observer.observe(element);
    layout();
    return () => observer.disconnect();
  }, [inlineAccounting, overflowOpen, modelLabel, reasoningEffort, hasProviders, overflowId]);

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
      <div class="composer-controls" ref={controls}>
        <div class="composer-selections" ref={selections}>
          {hasProviders === false ? (
            <button
              class="composer-selector composer-model-add-provider"
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
        </div>
        {inlineAccounting && (
          <div class="composer-accounting" ref={accounting}>
            {indicators}
          </div>
        )}
        <div class="composer-actions" ref={actions}>
          {!inlineAccounting && (
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
                  <div class="composer-accounting" ref={accounting}>
                    {indicators}
                  </div>
                </Popover>
              )}
            </div>
          )}
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
