import { useId, useLayoutEffect, useRef, useState } from "preact/hooks";
import {
  compactionSlider,
  compactionSliderPosition,
  compactionTriggerInput,
  defaultCompactionTriggerTokenCount,
  parseCompactionTriggerInput,
} from "../preferences/CompactionTriggerInput.ts";
import type { PreferencesState } from "../preferences/usePreferences.ts";
import "./compaction-setting.css";

const tokenFormat = new Intl.NumberFormat();

interface CompactionSettingProps {
  state: PreferencesState;
  draft: string;
  onDraftChange(value: string): void;
  onSaved(value: number | null): void;
}

export function CompactionSetting({
  state,
  draft,
  onDraftChange,
  onSaved,
}: CompactionSettingProps) {
  const id = useId();
  const saved = state.preferences?.compactionTriggerTokenCount ?? null;
  const error = state.errorFor("compactionTriggerTokenCount");
  const saveError = error === state.errorFor("workingDirectory") ? null : error;
  const saving = state.pendingFor === "compactionTriggerTokenCount";
  const disabled = !state.loaded || state.pending;
  const [validation, setValidation] = useState<string | null>(null);
  const [savedNotice, setSavedNotice] = useState(false);
  const [beforeExactEdit, setBeforeExactEdit] = useState<string | null>(null);
  const exactInput = useRef<HTMLInputElement>(null);
  const valueButton = useRef<HTMLButtonElement>(null);
  const focusValue = useRef(false);
  const editingExact = beforeExactEdit !== null;
  useLayoutEffect(() => {
    if (editingExact) {
      exactInput.current?.focus({ preventScroll: true });
      exactInput.current?.select();
    } else if (focusValue.current) {
      focusValue.current = false;
      valueButton.current?.focus({ preventScroll: true });
    }
  }, [editingExact]);

  const parsed = parseCompactionTriggerInput(draft);
  const usingDefault = parsed.valid && parsed.value === null;
  const tokens = (parsed.valid ? parsed.value : saved) ?? defaultCompactionTriggerTokenCount;
  const sliderPosition = compactionSliderPosition(tokens);
  const unchanged = parsed.valid && parsed.value === saved;
  const useDefaultDisabled = disabled || usingDefault;

  const edit = (value: string) => {
    onDraftChange(value);
    setValidation(null);
    setSavedNotice(false);
    state.clearError();
  };

  const save = async () => {
    if (disabled || unchanged) return;
    if (!parsed.valid) {
      setValidation(parsed.error);
      exactInput.current?.focus({ preventScroll: true });
      return;
    }
    setValidation(null);
    setSavedNotice(false);
    if (!(await state.saveCompactionTriggerTokenCount(parsed.value))) return;
    onSaved(parsed.value);
    focusValue.current = document.activeElement === exactInput.current;
    setBeforeExactEdit(null);
    setSavedNotice(true);
  };

  return (
    <form
      class="compaction-setting"
      aria-labelledby={`${id}-label`}
      aria-busy={saving}
      onSubmit={(event) => {
        event.preventDefault();
        void save();
      }}
    >
      <div class="compaction-setting-heading">
        <label id={`${id}-label`} htmlFor={id}>
          Compaction trigger
        </label>
        <div class="compaction-setting-value">
          {editingExact ? (
            <input
              ref={exactInput}
              class="compaction-exact-input"
              type="text"
              inputMode="numeric"
              autoComplete="off"
              aria-label="Exact compaction trigger in tokens"
              aria-invalid={validation ? "true" : undefined}
              aria-describedby={validation ? `${id}-error` : undefined}
              placeholder={String(defaultCompactionTriggerTokenCount)}
              value={draft}
              readOnly={disabled}
              onInput={(event) => edit(event.currentTarget.value)}
              onKeyDown={(event) => {
                if (event.key !== "Escape" || disabled) return;
                event.preventDefault();
                edit(beforeExactEdit);
                focusValue.current = true;
                setBeforeExactEdit(null);
              }}
            />
          ) : (
            <button
              ref={valueButton}
              type="button"
              disabled={disabled}
              aria-label={`Edit exact compaction trigger: ${tokenFormat.format(tokens)} tokens`}
              title="Edit exact value"
              onClick={() => setBeforeExactEdit(draft)}
            >
              {tokenFormat.format(tokens)} tokens
            </button>
          )}
          {usingDefault && <span class="compaction-setting-default">Default</span>}
        </div>
      </div>
      <input
        id={id}
        class="compaction-slider"
        type="range"
        {...compactionSlider}
        value={sliderPosition}
        aria-valuetext={`${tokenFormat.format(sliderPosition)} tokens`}
        aria-describedby={`${id}-help`}
        disabled={disabled}
        onInput={(event) => {
          const value = event.currentTarget.valueAsNumber;
          const next =
            value === defaultCompactionTriggerTokenCount && saved !== value ? null : value;
          edit(compactionTriggerInput(next));
          setBeforeExactEdit(null);
        }}
      />
      <div class="compaction-slider-limits" aria-hidden="true">
        <span>{compactionSlider.min / 1_000}k</span>
        <span>{compactionSlider.max / 1_000}k</span>
      </div>
      <div class="compaction-setting-footer">
        <p id={`${id}-help`} class="general-settings-description">
          Smaller model windows can compact earlier.
        </p>
        <div class="general-settings-actions">
          <button
            type="button"
            aria-disabled={useDefaultDisabled}
            onClick={() => {
              if (useDefaultDisabled) return;
              edit("");
              setBeforeExactEdit(null);
            }}
          >
            Use default
          </button>
          <button type="submit" aria-disabled={disabled || unchanged}>
            Save
          </button>
        </div>
      </div>
      <div class="general-settings-feedback" aria-live="polite" aria-atomic="true">
        {validation && (
          <p id={`${id}-error`} role="alert">
            {validation}
          </p>
        )}
        {saving && <p role="status">Saving…</p>}
        {savedNotice && !saving && <p role="status">Saved.</p>}
        {saveError && <p role="alert">{saveError}</p>}
      </div>
    </form>
  );
}
