import type { TargetedKeyboardEvent } from "preact";
import { useId, useLayoutEffect, useRef, useState } from "preact/hooks";
import { Icon } from "../components/Icon.tsx";
import { Popover, scrollOptionIntoView } from "../components/Popover.tsx";
import { Tooltip } from "../components/Tooltip.tsx";

interface ReasoningPickerProps {
  effort: string | null;
  options: readonly string[];
  onChange: (effort: string | null) => void;
  disabled: boolean;
}

function effortLabel(effort: string | null): string {
  switch (effort) {
    case null:
      return "Default";
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

export function ReasoningPicker({ effort, options, onChange, disabled }: ReasoningPickerProps) {
  const id = useId();
  const trigger = useRef<HTMLButtonElement>(null);
  const activeOption = useRef<HTMLButtonElement>(null);
  const [open, setOpen] = useState(false);
  const [activeIndex, setActiveIndex] = useState(0);
  const expanded = open && !disabled;
  const efforts = [...new Set(options)];
  const unknownEffort = effort !== null && !efforts.includes(effort);
  const choices = [
    { value: null, disabled: false },
    ...(unknownEffort ? [{ value: effort, disabled: true }] : []),
    ...efforts.map((value) => ({ value, disabled: false })),
  ];

  useLayoutEffect(() => {
    if (disabled) setOpen(false);
  }, [disabled]);

  useLayoutEffect(() => {
    if (!expanded) return;
    activeOption.current?.focus({ preventScroll: true });
    scrollOptionIntoView(activeOption.current);
  }, [expanded, activeIndex]);

  function show() {
    if (disabled) return;
    const selected = choices.findIndex((choice) => choice.value === effort && !choice.disabled);
    setActiveIndex(Math.max(0, selected));
    setOpen(true);
  }

  function choose(value: string | null) {
    if (disabled) return;
    setOpen(false);
    trigger.current?.focus({ preventScroll: true });
    onChange(value);
  }

  function navigate(event: TargetedKeyboardEvent<HTMLButtonElement>, index: number) {
    if (event.key === "Tab") {
      // Forward Tab leaves naturally; Shift+Tab returns to the preceding trigger.
      if (event.shiftKey) {
        event.preventDefault();
        setOpen(false);
        trigger.current?.focus({ preventScroll: true });
      }
      return;
    }
    if (!["ArrowDown", "ArrowUp", "Home", "End"].includes(event.key)) return;
    event.preventDefault();
    const available = choices.flatMap((choice, index) => (choice.disabled ? [] : [index]));
    const current = available.indexOf(index);
    const next =
      event.key === "Home"
        ? 0
        : event.key === "End"
          ? available.length - 1
          : (current + (event.key === "ArrowDown" ? 1 : -1) + available.length) % available.length;
    setActiveIndex(available[next]!);
  }

  return (
    <div class="composer-reasoning">
      <Tooltip content="Reasoning effort" disabled={expanded}>
        <button
          ref={trigger}
          class="composer-selector composer-reasoning-trigger"
          type="button"
          disabled={disabled}
          aria-label={`Reasoning effort: ${effortLabel(effort)}`}
          aria-haspopup="listbox"
          aria-expanded={expanded}
          aria-controls={expanded ? id : undefined}
          onClick={() => (expanded ? setOpen(false) : show())}
          onKeyDown={(event) => {
            if (event.key !== "ArrowDown" && event.key !== "ArrowUp") return;
            event.preventDefault();
            show();
          }}
        >
          <span>{effortLabel(effort)}</span>
          <Icon name="chevron" />
        </button>
      </Tooltip>
      {expanded && (
        <Popover
          anchor={trigger}
          initialFocus={activeOption}
          id={id}
          class="composer-reasoning-popover"
          placement="above"
          role="listbox"
          label="Reasoning effort"
          onClose={() => setOpen(false)}
        >
          {choices.map((choice, index) => (
            <button
              key={choice.value === null ? "default" : `effort:${choice.value}`}
              ref={index === activeIndex ? activeOption : null}
              class={`composer-reasoning-option${index === activeIndex ? " is-active" : ""}`}
              type="button"
              role="option"
              aria-selected={choice.value === effort}
              disabled={choice.disabled}
              tabIndex={index === activeIndex ? 0 : -1}
              onFocus={() => setActiveIndex(index)}
              onKeyDown={(event) => navigate(event, index)}
              onClick={() => choose(choice.value)}
            >
              <span>{effortLabel(choice.value)}</span>
              {choice.value === effort && <Icon name="check" />}
            </button>
          ))}
        </Popover>
      )}
    </div>
  );
}
