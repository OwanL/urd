import type { RefObject } from "preact";
import { useId, useState } from "preact/hooks";
import {
  compactionTriggerInput,
  parseCompactionTriggerInput,
} from "../preferences/CompactionTriggerInput.ts";
import type { PreferencesState } from "../preferences/usePreferences.ts";
import { WorkingDirectorySetting } from "./WorkingDirectorySetting.tsx";
import { LanHostingSetting } from "./LanHostingSetting.tsx";
import { BitwardenSetting } from "./BitwardenSetting.tsx";

interface GeneralSettingsProps {
  state: PreferencesState;
  buttonRef: RefObject<HTMLButtonElement | null>;
  compactionTriggerDraft: string;
  onCompactionTriggerDraftChange(value: string): void;
  onCompactionTriggerSaved(value: number | null): void;
}

export function GeneralSettings({
  state,
  buttonRef,
  compactionTriggerDraft,
  onCompactionTriggerDraftChange,
  onCompactionTriggerSaved,
}: GeneralSettingsProps) {
  const compactionTriggerTokenCount = state.preferences?.compactionTriggerTokenCount ?? null;
  // Load failures apply to every preference; the working directory field already reports them.
  const triggerError =
    state.errorFor("compactionTriggerTokenCount") === state.errorFor("workingDirectory")
      ? null
      : state.errorFor("compactionTriggerTokenCount");
  const [triggerValidation, setTriggerValidation] = useState<string | null>(null);
  const [triggerSaving, setTriggerSaving] = useState(false);
  const [triggerSaved, setTriggerSaved] = useState(false);
  const triggerId = useId();
  const triggerErrorId = `${triggerId}-error`;

  const editTrigger = (value: string) => {
    onCompactionTriggerDraftChange(value);
    setTriggerValidation(null);
    setTriggerSaved(false);
    state.clearError();
  };

  const saveTrigger = async () => {
    const result = parseCompactionTriggerInput(compactionTriggerDraft);
    if (!result.valid) {
      setTriggerValidation(result.error);
      return;
    }
    if (state.pending || triggerSaving) return;

    setTriggerValidation(null);
    setTriggerSaved(false);
    setTriggerSaving(true);
    if (await state.saveCompactionTriggerTokenCount(result.value)) {
      onCompactionTriggerSaved(result.value);
      setTriggerSaved(true);
    }
    setTriggerSaving(false);
  };

  return (
    <section
      class="general-settings"
      aria-label="General"
      aria-busy={state.loading || state.pending || triggerSaving}
    >
      <WorkingDirectorySetting state={state} buttonRef={buttonRef} />
      <div class="general-settings-field compaction-trigger-setting" aria-busy={triggerSaving}>
        <label htmlFor={`${triggerId}-input`}>Compaction trigger (tokens)</label>
        <div class="compaction-trigger-control">
          <input
            id={`${triggerId}-input`}
            type="text"
            inputMode="numeric"
            autoComplete="off"
            placeholder="Automatic"
            value={compactionTriggerDraft}
            aria-invalid={triggerValidation ? "true" : undefined}
            aria-describedby={triggerValidation ? triggerErrorId : undefined}
            disabled={!state.loaded || state.pending || triggerSaving}
            onInput={(event) => editTrigger(event.currentTarget.value)}
          />
          <button
            type="button"
            disabled={
              !state.loaded ||
              state.pending ||
              triggerSaving ||
              (compactionTriggerDraft === "" && compactionTriggerTokenCount === null)
            }
            onClick={() => editTrigger("")}
          >
            Set automatic
          </button>
          <button
            type="button"
            disabled={
              !state.loaded ||
              state.pending ||
              triggerSaving ||
              compactionTriggerDraft === compactionTriggerInput(compactionTriggerTokenCount)
            }
            onClick={() => void saveTrigger()}
          >
            Save
          </button>
        </div>
        <div class="general-settings-feedback" aria-live="polite" aria-atomic="true">
          {triggerValidation && (
            <p id={triggerErrorId} role="alert">
              {triggerValidation}
            </p>
          )}
          {triggerSaving && <p role="status">Saving…</p>}
          {triggerSaved && !triggerSaving && <p role="status">Saved.</p>}
          {triggerError && <p role="alert">{triggerError}</p>}
        </div>
      </div>
      {location.hostname === "127.0.0.1" && (
        <>
          <LanHostingSetting />
          <BitwardenSetting />
        </>
      )}
    </section>
  );
}
