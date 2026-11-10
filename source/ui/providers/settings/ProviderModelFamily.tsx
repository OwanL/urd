import { useId, useLayoutEffect, useRef, useState } from "preact/hooks";
import { Icon } from "../../components/Icon.tsx";
import { Popover } from "../../components/Popover.tsx";
import type { ModelFamily } from "../ModelSettings.ts";

const families: Readonly<Record<ModelFamily, string>> = {
  openAI: "OpenAI",
  anthropic: "Anthropic",
  google: "Google",
  other: "Other",
};

// A compact explicit choice; the model ID never supplies a family default.
export function ProviderModelFamily({
  family,
  modelName,
  disabled,
  onChange,
}: {
  family: ModelFamily;
  modelName: string;
  disabled: boolean;
  onChange: (family: ModelFamily) => void;
}) {
  const id = useId();
  const trigger = useRef<HTMLButtonElement>(null);
  const selected = useRef<HTMLButtonElement>(null);
  const [open, setOpen] = useState(false);
  useLayoutEffect(() => {
    if (disabled) setOpen(false);
  }, [disabled]);
  return (
    <>
      <button
        ref={trigger}
        type="button"
        disabled={disabled}
        aria-label={`${modelName} family: ${families[family]}`}
        aria-haspopup="dialog"
        aria-expanded={open && !disabled}
        aria-controls={open && !disabled ? id : undefined}
        onClick={() => setOpen(!open)}
      >
        Family: {families[family]}
      </button>
      {open && !disabled && (
        <Popover
          anchor={trigger}
          initialFocus={selected}
          id={id}
          class="provider-model-family"
          label={`${modelName} family`}
          onClose={() => setOpen(false)}
        >
          {(Object.keys(families) as ModelFamily[]).map((value) => (
            <button
              ref={value === family ? selected : null}
              key={value}
              type="button"
              aria-pressed={value === family}
              onClick={() => {
                setOpen(false);
                trigger.current?.focus({ preventScroll: true });
                onChange(value);
              }}
            >
              <span>{families[value]}</span>
              {value === family && <Icon name="check" />}
            </button>
          ))}
        </Popover>
      )}
    </>
  );
}
