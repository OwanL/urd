import { useId, useState } from "preact/hooks";
import { parseSubagentDepthInput } from "../preferences/SubagentDepthInput.ts";
import type { PreferencesState } from "../preferences/usePreferences.ts";

export function MaxSubagentDepth({ state }: { state: PreferencesState }) {
  const id = useId();
  const saved = state.preferences?.maxSubagentDepth;
  const [draft, setDraft] = useState<{ value: string; base: number } | null>(null);
  const [validation, setValidation] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const value = draft?.value ?? (saved === undefined ? "" : String(saved));
  const stale = draft !== null && saved !== draft.base;
  const error = state.loaded ? state.errorFor("maxSubagentDepth") : null;
  const disabled = saved === undefined || state.pending || saving;
  const unchanged = value === String(saved);

  const save = async () => {
    if (disabled || stale || unchanged) return;
    const depth = parseSubagentDepthInput(value);
    if (depth === null) {
      setValidation("Enter a whole number from 0 to 2,147,483,647.");
      return;
    }
    setValidation(null);
    setSaving(true);
    if (await state.saveMaxSubagentDepth(depth)) setDraft(null);
    setSaving(false);
  };

  return (
    <form
      class="general-setting-form"
      aria-busy={saving}
      onSubmit={(event) => {
        event.preventDefault();
        void save();
      }}
    >
      <div class="general-settings-field">
        <label htmlFor={id}>Max subagent depth</label>
        <p id={`${id}-help`} class="general-settings-description">
          The main agent is not counted. 0 disables delegation.
        </p>
      </div>
      <div class="settings-number-control">
        <input
          id={id}
          type="text"
          inputMode="numeric"
          autoComplete="off"
          value={value}
          aria-describedby={validation ? `${id}-help ${id}-error` : `${id}-help`}
          aria-invalid={validation ? "true" : undefined}
          disabled={disabled}
          onInput={(event) => {
            if (saved === undefined) return;
            setDraft({ value: event.currentTarget.value, base: draft?.base ?? saved });
            setValidation(null);
            state.clearError();
          }}
        />
        <button type="submit" disabled={disabled || stale || unchanged}>
          Save
        </button>
        {stale && (
          <button
            type="button"
            onClick={() => {
              setDraft(null);
              setValidation(null);
            }}
          >
            Use saved value
          </button>
        )}
      </div>
      <div class="general-settings-feedback" aria-live="polite">
        {stale && <p>Saved depth changed. Use the saved value before editing again.</p>}
        {validation && (
          <p id={`${id}-error`} role="alert">
            {validation}
          </p>
        )}
        {(saving || state.pendingFor === "maxSubagentDepth") && <p role="status">Saving…</p>}
        {error && <p role="alert">{error}</p>}
      </div>
    </form>
  );
}
